// =============================================================================
// YmfReader — GTA5 _manifest.ymf 파일 파서 (학습용, 독립 실행)
//
// 이 파일 하나에 파싱에 필요한 모든 타입과 로직이 담겨 있습니다.
// CodeWalker 의존 없이 동작하며, 파싱 과정을 단계별로 출력합니다.
//
// 실행:  dotnet run                           (기본 디렉토리 사용)
//        dotnet run -- "C:\path\to\dir"       (디렉토리 지정)
//        dotnet run -- "C:\path\to\file.ymf" --verbose  (상세 출력)
//
// ─────────────────────────────────────────────────────────────────────────────
//
//  .ymf 파일 포맷 개요
//  ====================
//  _manifest.ymf 는 스트리밍 RPF 의 매니페스트로, 대부분 PSO 포맷입니다.
//  일부 props RPF 내 _manifest.ymf 는 RBF 포맷(HDTxd 바인딩만 포함)입니다.
//
//  PSO (Platform Serialized Object) 포맷:
//    - 빅엔디안(Big-Endian) 바이너리
//    - 여러 섹션(Chunk)으로 구성: PSIN | PMAP | PSCH | STRF | STRS | PSIG | CHKS
//    - 각 섹션: [ident 4 bytes][length 4 bytes][body...]
//
//  주요 섹션:
//    PSIN: 직렬화된 구조체 데이터 블록 (빅엔디안)
//    PMAP: 블록 맵 — RootId + 블록 배열 (id → PSIN 내 오프셋+크기)
//    STRF: 파일/폴더 경로 문자열 (null-terminated ASCII, 빅엔디안 아님)
//    STRS: 이름 문자열 (null-terminated ASCII)
//
//  배열 포인터 인코딩 (Array_Structure / Array_uint, 16 bytes):
//    [0x00] u64 Pointer  (빅엔디안)
//           bits[0..11]  = PointerDataId    (1-based 블록 ID)
//           bits[12..31] = PointerDataOffset (블록 내 바이트 오프셋)
//    [0x08] u16 Count1   (빅엔디안)
//    [0x0A] u16 Count2
//    [0x0C] u32 Unk1
//
//    해석: data_abs_offset = blocks[PointerDataId - 1].Offset + PointerDataOffset
//    (PSIN 섹션 데이터의 절대 오프셋, PSIN 헤더 8바이트 포함)
//
//  루트 구조체 CPackFileMetaData (96 bytes):
//    [0x00] Array_Structure MapDataGroups
//    [0x10] Array_Structure HDTxdBindingArray
//    [0x20] Array_Structure imapDependencies
//    [0x30] Array_Structure imapDependencies_2
//    [0x40] Array_Structure itypDependencies_2
//    [0x50] Array_Structure Interiors
//
//  해시: 파일 이름은 Jenkins/JOAAT 해시로 저장됩니다.
//        STRF/STRS 문자열로 역조회(reverse-lookup) 테이블을 만들어 표시합니다.
//
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YmfReader
{

// =============================================================================
// ── 1. 빅엔디안 리더 ─────────────────────────────────────────────────────────
//
//  PSO 포맷은 빅엔디안입니다. BitConverter 는 시스템 엔디안(LE)을 사용하므로
//  수동으로 바이트를 조합합니다.
// =============================================================================

static class BE
{
    public static uint   U32(byte[] d, int off) =>
        ((uint)d[off] << 24) | ((uint)d[off+1] << 16) | ((uint)d[off+2] << 8) | d[off+3];

    public static ulong  U64(byte[] d, int off) =>
        ((ulong)U32(d, off) << 32) | U32(d, off + 4);

    public static ushort U16(byte[] d, int off) =>
        (ushort)(((ushort)d[off] << 8) | d[off+1]);

    public static int I32(byte[] d, int off) => (int)U32(d, off);

    // null-terminated ASCII 문자열 읽기 (리틀/빅엔디안 무관)
    public static string NullStr(byte[] d, int off, int maxLen = 512)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxLen && off + i < d.Length; i++)
        {
            byte b = d[off + i];
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}


// =============================================================================
// ── 2. Jenkins/JOAAT 해시 ────────────────────────────────────────────────────
//
//  GTA5 는 파일 이름을 Jenkins One-At-A-Time 해시(소문자)로 저장합니다.
//  STRF/STRS 의 문자열을 해시 → 문자열 역조회 테이블로 변환합니다.
// =============================================================================

static class Jenkins
{
    public static uint Hash(string s)
    {
        uint h = 0;
        foreach (char c in s.ToLowerInvariant())
        {
            h += (byte)c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }
}


// =============================================================================
// ── 3. PSO 섹션 파싱 ─────────────────────────────────────────────────────────
// =============================================================================

// PMAP 블록 항목 (각 블록의 PSIN 내 위치/크기)
class PsoBlock
{
    public uint NameHash;
    public int  Offset;   // PSIN 데이터(헤더 포함) 내 절대 오프셋
    public int  Length;
}

class PsoFile
{
    // 파싱된 섹션 데이터 (헤더 8바이트 포함)
    public byte[]?     PsinData;
    public int         RootBlockId;
    public PsoBlock[]? Blocks;
    public string[]    StrfStrings = Array.Empty<string>();
    public string[]    StrsStrings = Array.Empty<string>();

    // 해시 → 문자열 역조회 테이블 (STRF + STRS 합산)
    public Dictionary<uint, string> HashToName = new();

    const uint IDENT_PSIN = 0x5053494E;  // "PSIN"
    const uint IDENT_PMAP = 0x504D4150;  // "PMAP"
    const uint IDENT_STRF = 0x53545246;  // "STRF"
    const uint IDENT_STRS = 0x53545253;  // "STRS"
    const uint IDENT_RBF0 = 0x52424600;  // "RBF?" (RBF 포맷 감지용)

    // 파일이 PSO 포맷인지 확인
    public static bool IsPso(byte[] data) =>
        data.Length >= 4 && BE.U32(data, 0) == IDENT_PSIN;

    public static bool IsRbf(byte[] data) =>
        data.Length >= 4 && (BE.U32(data, 0) & 0xFFFFFF00) == IDENT_RBF0;

    public void Load(byte[] data)
    {
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            uint ident  = BE.U32(data, pos);
            int  length = BE.I32(data, pos + 4);
            if (length <= 0 || pos + length > data.Length) break;

            switch (ident)
            {
                case IDENT_PSIN:
                    PsinData = new byte[length];
                    Array.Copy(data, pos, PsinData, 0, length);
                    break;

                case IDENT_PMAP:
                    ParsePmap(data, pos, length);
                    break;

                case IDENT_STRF:
                    StrfStrings = ParseStringSection(data, pos + 8, length - 8);
                    break;

                case IDENT_STRS:
                    StrsStrings = ParseStringSection(data, pos + 8, length - 8);
                    break;
            }
            pos += length;
        }

        // 해시 역조회 테이블 구축
        foreach (var s in StrfStrings.Concat(StrsStrings))
        {
            if (string.IsNullOrEmpty(s)) continue;
            uint h = Jenkins.Hash(s);
            HashToName.TryAdd(h, s);
            // 확장자 없는 기본 이름도 등록
            string bn = Path.GetFileNameWithoutExtension(s);
            if (bn != s) HashToName.TryAdd(Jenkins.Hash(bn), bn);
        }
    }

    // ── PMAP 파싱 ─────────────────────────────────────────────────────────
    void ParsePmap(byte[] data, int secOff, int secLen)
    {
        // [0] ident(4) [4] length(4) [8] RootId(4) [12] Count(2) [14] Unk(2)
        RootBlockId = BE.I32(data, secOff + 8);
        short count = (short)BE.U16(data, secOff + 12);

        if (count <= 0)
        {
            // 일부 파일은 여기서 추가 헤더를 가짐 (16-entry 헤더 변형)
            // skip — 빈 블록으로 처리
            Blocks = Array.Empty<PsoBlock>();
            return;
        }

        Blocks = new PsoBlock[count];
        int entOff = secOff + 16;
        for (int i = 0; i < count; i++, entOff += 16)
        {
            Blocks[i] = new PsoBlock
            {
                NameHash = BE.U32(data, entOff),
                Offset   = BE.I32(data, entOff + 4),
                Length   = BE.I32(data, entOff + 12),
            };
        }
    }

    // ── STRF/STRS 문자열 섹션 파싱 ────────────────────────────────────────
    static string[] ParseStringSection(byte[] data, int bodyOff, int bodyLen)
    {
        var list = new List<string>();
        int pos = bodyOff;
        int end = bodyOff + bodyLen;
        while (pos < end)
        {
            int start = pos;
            while (pos < end && data[pos] != 0) pos++;
            if (pos > start)
                list.Add(Encoding.ASCII.GetString(data, start, pos - start));
            pos++; // null 건너뜀
        }
        return list.ToArray();
    }

    // ── PSIN 데이터에서 구조체 읽기 ───────────────────────────────────────
    public (int absOff, bool ok) RootItemOffset()
    {
        if (PsinData == null || Blocks == null || Blocks.Length == 0) return (-1, false);
        var block = Block(RootBlockId);
        if (block == null) return (-1, false);
        return (block.Offset, true);
    }

    public PsoBlock? Block(int id)
    {
        if (Blocks == null) return null;
        int idx = id - 1;
        return (idx >= 0 && idx < Blocks.Length) ? Blocks[idx] : null;
    }

    // Array_Structure / Array_uint 포인터 해석 → (absOffset, count, ok)
    //
    // PSO 포맷 주의:
    //   Array_Structure.Pointer 는 8 bytes (big-endian u64) 이지만,
    //   CodeWalker 의 SwapBytes(ulong) 는 32-bit 블록을 스왑하지 않는다.
    //   결과적으로 PointerDataId/PointerDataOffset 는
    //   Pointer 의 앞 4 bytes (BE u32) 에 인코딩된다:
    //     dataId  = BE.U32(bytes 0-3) & 0xFFF
    //     dataOff = (BE.U32(bytes 0-3) >> 12) & 0xFFFFF
    public (int off, int count, bool ok) ResolveArray(int ptrFieldAbsOff)
    {
        if (PsinData == null || Blocks == null) return (-1, 0, false);

        uint ptrLo  = BE.U32(PsinData, ptrFieldAbsOff);      // 앞 4 bytes 만 사용
        int  count  = (int)BE.U16(PsinData, ptrFieldAbsOff + 8);

        if (ptrLo == 0 || count == 0) return (-1, 0, false);

        int  dataId  = (int)(ptrLo & 0xFFF);
        int  dataOff = (int)((ptrLo >> 12) & 0xFFFFF);
        var  block   = Block(dataId);
        if (block == null) return (-1, 0, false);

        return (block.Offset + dataOff, count, true);
    }

    // 해시 → 문자열 (없으면 "0x{hash:X8}")
    public string Name(uint hash)
    {
        if (hash == 0) return "(null)";
        return HashToName.TryGetValue(hash, out var s) ? s : $"0x{hash:X8}";
    }

    // PSIN 절대 오프셋에서 u32 읽기 (big-endian)
    public uint U32At(int absOff) =>
        PsinData != null && absOff + 4 <= PsinData.Length ? BE.U32(PsinData, absOff) : 0;

    public ushort U16At(int absOff) =>
        PsinData != null && absOff + 2 <= PsinData.Length ? BE.U16(PsinData, absOff) : (ushort)0;

    // PsoChar64 (64 bytes, null-terminated ASCII)
    public string Char64At(int absOff) =>
        PsinData != null ? BE.NullStr(PsinData, absOff, 64) : "";
}


// =============================================================================
// ── 4. CMapDataGroup 정보 ────────────────────────────────────────────────────
//
//  CMapDataGroup (56 bytes, 빅엔디안):
//    [0x00] u32  Name          — .ymap 파일 이름 해시
//    [0x04] u32  Unused0
//    [0x08] Array_uint Bounds  — .ybn 파일 이름 해시 배열 (16 bytes)
//    [0x18] u16  Flags
//    [0x1A] u16  Unused1
//    [0x1C] u32  Unused2
//    [0x20] Array_uint WeatherTypes  — 날씨 타입 해시 배열 (16 bytes)
//    [0x30] u32  HoursOnOff   — 비트마스크 (0=항상, 나머지=시간대)
//    [0x34] u32  Unused3
// =============================================================================

class MapDataGroupInfo
{
    public string Name = "";
    public uint   NameHash;
    public string[] Bounds       = Array.Empty<string>();
    public string[] WeatherTypes = Array.Empty<string>();
    public ushort Flags;
    public uint   HoursOnOff;

    public static MapDataGroupInfo Read(PsoFile pso, int absOff)
    {
        uint nameHash = pso.U32At(absOff + 0x00);
        var  bounds   = ReadHashArray(pso, absOff + 0x08);
        var  weather  = ReadHashArray(pso, absOff + 0x20);

        return new MapDataGroupInfo
        {
            NameHash     = nameHash,
            Name         = pso.Name(nameHash),
            Bounds       = bounds.Select(h => pso.Name(h)).ToArray(),
            WeatherTypes = weather.Select(h => pso.Name(h)).ToArray(),
            Flags        = pso.U16At(absOff + 0x18),
            HoursOnOff   = pso.U32At(absOff + 0x30),
        };
    }

    // Array_uint → uint[] 해시 배열
    static uint[] ReadHashArray(PsoFile pso, int arrAbsOff)
    {
        var (off, count, ok) = pso.ResolveArray(arrAbsOff);
        if (!ok || pso.PsinData == null) return Array.Empty<uint>();
        var result = new uint[count];
        for (int i = 0; i < count; i++)
            result[i] = BE.U32(pso.PsinData, off + i * 4);
        return result;
    }

    public void Print(string pad, bool verbose)
    {
        string hours = HoursOnOff == 0
            ? "항상"
            : $"비트마스크=0x{HoursOnOff:X8}";

        Console.WriteLine($"{pad}  {Name}  (hash=0x{NameHash:X8}  flags=0x{Flags:X4}  hours={hours})");
        if (verbose)
        {
            if (Bounds.Length > 0)
                Console.WriteLine($"{pad}    ybn bounds ({Bounds.Length}): {string.Join(", ", Bounds.Take(5))}{(Bounds.Length > 5 ? "…" : "")}");
            if (WeatherTypes.Length > 0)
                Console.WriteLine($"{pad}    weatherTypes ({WeatherTypes.Length}): {string.Join(", ", WeatherTypes)}");
        }
    }
}


// =============================================================================
// ── 5. CImapDependency 정보 ──────────────────────────────────────────────────
//
//  CImapDependency (12 bytes, 빅엔디안):
//    [0x00] u32  imapName     — .ymap 해시
//    [0x04] u32  itypName     — .ytyp 해시
//    [0x08] u32  packFileName — .rpf 해시
// =============================================================================

class ImapDependencyInfo
{
    public string ImapName     = "";
    public string ItypName     = "";
    public string PackFileName = "";

    public static ImapDependencyInfo Read(PsoFile pso, int absOff)
    {
        return new ImapDependencyInfo
        {
            ImapName     = pso.Name(pso.U32At(absOff + 0x00)),
            ItypName     = pso.Name(pso.U32At(absOff + 0x04)),
            PackFileName = pso.Name(pso.U32At(absOff + 0x08)),
        };
    }

    public void Print(string pad)
    {
        Console.WriteLine($"{pad}  ymap={ImapName}  →  ytyp={ItypName}  pack={PackFileName}");
    }
}


// =============================================================================
// ── 6. CImapDependencies / CItypDependencies (v2) ────────────────────────────
//
//  CImapDependencies (24 bytes, 빅엔디안):
//    [0x00] u32  imapName
//    [0x04] u16  manifestFlags
//    [0x06] u16  Unused0
//    [0x08] Array_uint itypDepArray (16 bytes) — .ytyp 해시 배열
//
//  CItypDependencies (24 bytes, 빅엔디안):
//    [0x00] u32  itypName
//    [0x04] u16  manifestFlags
//    [0x06] u16  Unused0
//    [0x08] Array_uint itypDepArray (16 bytes)
// =============================================================================

class ImapDependencies2Info
{
    public string   ImapName = "";
    public ushort   ManifestFlags;
    public string[] ItypDeps = Array.Empty<string>();

    public static ImapDependencies2Info Read(PsoFile pso, int absOff)
    {
        uint hash = pso.U32At(absOff + 0x00);
        var (off, count, ok) = pso.ResolveArray(absOff + 0x08);

        string[] deps = Array.Empty<string>();
        if (ok && pso.PsinData != null)
        {
            deps = new string[count];
            for (int i = 0; i < count; i++)
                deps[i] = pso.Name(BE.U32(pso.PsinData, off + i * 4));
        }

        return new ImapDependencies2Info
        {
            ImapName      = pso.Name(hash),
            ManifestFlags = pso.U16At(absOff + 0x04),
            ItypDeps      = deps,
        };
    }

    public void Print(string pad, bool verbose)
    {
        Console.WriteLine($"{pad}  ymap={ImapName}  flags=0x{ManifestFlags:X4}  ytyps({ItypDeps.Length})");
        if (verbose && ItypDeps.Length > 0)
            Console.WriteLine($"{pad}    deps: {string.Join(", ", ItypDeps.Take(5))}{(ItypDeps.Length > 5 ? "…" : "")}");
    }
}

class ItypDependencies2Info
{
    public string   ItypName = "";
    public ushort   ManifestFlags;
    public string[] ItypDeps = Array.Empty<string>();

    public static ItypDependencies2Info Read(PsoFile pso, int absOff)
    {
        uint hash = pso.U32At(absOff + 0x00);
        var (off, count, ok) = pso.ResolveArray(absOff + 0x08);

        string[] deps = Array.Empty<string>();
        if (ok && pso.PsinData != null)
        {
            deps = new string[count];
            for (int i = 0; i < count; i++)
                deps[i] = pso.Name(BE.U32(pso.PsinData, off + i * 4));
        }

        return new ItypDependencies2Info
        {
            ItypName      = pso.Name(hash),
            ManifestFlags = pso.U16At(absOff + 0x04),
            ItypDeps      = deps,
        };
    }

    public void Print(string pad, bool verbose)
    {
        Console.WriteLine($"{pad}  ytyp={ItypName}  flags=0x{ManifestFlags:X4}  deps({ItypDeps.Length})");
        if (verbose && ItypDeps.Length > 0)
            Console.WriteLine($"{pad}    deps: {string.Join(", ", ItypDeps.Take(5))}{(ItypDeps.Length > 5 ? "…" : "")}");
    }
}


// =============================================================================
// ── 7. CHDTxdAssetBinding 정보 ───────────────────────────────────────────────
//
//  CHDTxdAssetBinding (132 bytes, 빅엔디안):
//    [0x00] u8   assetType
//    [0x01] u8   Unused01
//    [0x02] u16  Unused02
//    [0x04] char[64] targetAsset  — 바인딩 대상 에셋 이름 (ASCII)
//    [0x44] char[64] HDTxd        — HD 텍스처 딕셔너리 이름 (ASCII)
// =============================================================================

class HdTxdAssetBindingInfo
{
    public byte   AssetType;
    public string TargetAsset = "";
    public string HdTxd       = "";

    public static HdTxdAssetBindingInfo Read(PsoFile pso, int absOff)
    {
        if (pso.PsinData == null) return new HdTxdAssetBindingInfo();
        return new HdTxdAssetBindingInfo
        {
            AssetType   = pso.PsinData[absOff + 0x00],
            TargetAsset = BE.NullStr(pso.PsinData, absOff + 0x04, 64),
            HdTxd       = BE.NullStr(pso.PsinData, absOff + 0x44, 64),
        };
    }

    public void Print(string pad)
    {
        Console.WriteLine($"{pad}  type={AssetType}  target={TargetAsset}  hdtxd={HdTxd}");
    }
}


// =============================================================================
// ── 8. CInteriorBoundsFiles 정보 ─────────────────────────────────────────────
//
//  CInteriorBoundsFiles (24 bytes, 빅엔디안):
//    [0x00] u32  Name           — 인테리어 이름 해시
//    [0x04] u32  Unused0
//    [0x08] Array_uint Bounds   — .ybn 해시 배열 (16 bytes)
// =============================================================================

class InteriorInfo
{
    public string   Name   = "";
    public string[] Bounds = Array.Empty<string>();

    public static InteriorInfo Read(PsoFile pso, int absOff)
    {
        uint hash = pso.U32At(absOff + 0x00);
        var (off, count, ok) = pso.ResolveArray(absOff + 0x08);

        string[] bounds = Array.Empty<string>();
        if (ok && pso.PsinData != null)
        {
            bounds = new string[count];
            for (int i = 0; i < count; i++)
                bounds[i] = pso.Name(BE.U32(pso.PsinData, off + i * 4));
        }

        return new InteriorInfo
        {
            Name   = pso.Name(hash),
            Bounds = bounds,
        };
    }

    public void Print(string pad, bool verbose)
    {
        Console.WriteLine($"{pad}  {Name}  bounds({Bounds.Length})");
        if (verbose && Bounds.Length > 0)
            Console.WriteLine($"{pad}    ybn: {string.Join(", ", Bounds)}");
    }
}


// =============================================================================
// ── 9. YmfFileInfo — .ymf 파일 전체 파싱 결과 ───────────────────────────────
//
//  CPackFileMetaData (96 bytes, PSIN 루트 블록):
//    [0x00] Array_Structure MapDataGroups        — ymap 그룹 배열 (각 56 bytes)
//    [0x10] Array_Structure HDTxdBindingArray    — HD TXD 바인딩 (각 132 bytes)
//    [0x20] Array_Structure imapDependencies     — ymap→ytyp 의존성 (각 12 bytes)
//    [0x30] Array_Structure imapDependencies_2   — ymap→ytyp 의존성 v2 (각 24 bytes)
//    [0x40] Array_Structure itypDependencies_2   — ytyp 의존성 (각 24 bytes)
//    [0x50] Array_Structure Interiors            — 인테리어 (각 24 bytes)
// =============================================================================

class YmfFileInfo
{
    public int    FileSize;
    public string FormatType = "";

    public MapDataGroupInfo[]       MapDataGroups       = Array.Empty<MapDataGroupInfo>();
    public ImapDependencyInfo[]     ImapDependencies    = Array.Empty<ImapDependencyInfo>();
    public ImapDependencies2Info[]  ImapDependencies2   = Array.Empty<ImapDependencies2Info>();
    public ItypDependencies2Info[]  ItypDependencies2   = Array.Empty<ItypDependencies2Info>();
    public HdTxdAssetBindingInfo[]  HdTxdBindings       = Array.Empty<HdTxdAssetBindingInfo>();
    public InteriorInfo[]           Interiors           = Array.Empty<InteriorInfo>();

    // STRF 문자열 목록 (파일 경로)
    public string[] StrfStrings = Array.Empty<string>();

    public static YmfFileInfo Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var result  = new YmfFileInfo { FileSize = data.Length };

        if (PsoFile.IsRbf(data))
        {
            result.FormatType = "RBF";
            return result; // RBF: HDTxd 바인딩만, 구조 파싱 생략
        }
        if (!PsoFile.IsPso(data))
        {
            result.FormatType = "UNKNOWN";
            return result;
        }

        result.FormatType = "PSO";

        var pso = new PsoFile();
        pso.Load(data);

        result.StrfStrings = pso.StrfStrings;

        if (pso.PsinData == null || pso.Blocks == null || pso.Blocks.Length == 0)
            return result;

        // ── 루트 블록 위치 ─────────────────────────────────────────────────
        var (rootOff, ok) = pso.RootItemOffset();
        if (!ok) return result;

        // ── MapDataGroups ────────────────────────────────────────────────────
        // CPackFileMetaData[0x00] = Array_Structure MapDataGroups (각 56 bytes)
        result.MapDataGroups = ReadStructArray(pso, rootOff + 0x00, 56,
            (p, off) => MapDataGroupInfo.Read(p, off));

        // ── HDTxdBindingArray ─────────────────────────────────────────────────
        // [0x10] Array_Structure HDTxdBindingArray (각 132 bytes)
        result.HdTxdBindings = ReadStructArray(pso, rootOff + 0x10, 132,
            (p, off) => HdTxdAssetBindingInfo.Read(p, off));

        // ── imapDependencies ─────────────────────────────────────────────────
        // [0x20] Array_Structure imapDependencies (각 12 bytes)
        result.ImapDependencies = ReadStructArray(pso, rootOff + 0x20, 12,
            (p, off) => ImapDependencyInfo.Read(p, off));

        // ── imapDependencies_2 ───────────────────────────────────────────────
        // [0x30] Array_Structure imapDependencies_2 (각 24 bytes)
        result.ImapDependencies2 = ReadStructArray(pso, rootOff + 0x30, 24,
            (p, off) => ImapDependencies2Info.Read(p, off));

        // ── itypDependencies_2 ───────────────────────────────────────────────
        // [0x40] Array_Structure itypDependencies_2 (각 24 bytes)
        result.ItypDependencies2 = ReadStructArray(pso, rootOff + 0x40, 24,
            (p, off) => ItypDependencies2Info.Read(p, off));

        // ── Interiors ─────────────────────────────────────────────────────────
        // [0x50] Array_Structure Interiors (각 24 bytes)
        result.Interiors = ReadStructArray(pso, rootOff + 0x50, 24,
            (p, off) => InteriorInfo.Read(p, off));

        return result;
    }

    // Array_Structure → T[] 읽기 (고정 크기 구조체)
    static T[] ReadStructArray<T>(PsoFile pso, int arrAbsOff, int itemSize, Func<PsoFile, int, T> read)
    {
        var (off, count, ok) = pso.ResolveArray(arrAbsOff);
        if (!ok || pso.PsinData == null) return Array.Empty<T>();
        if (off + count * itemSize > pso.PsinData.Length) return Array.Empty<T>();

        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
            list.Add(read(pso, off + i * itemSize));
        return list.ToArray();
    }

    public void Print(string pad, bool verbose)
    {
        Console.WriteLine($"{pad}  포맷: {FormatType}  크기: {FileSize:N0} bytes");

        if (FormatType != "PSO")
        {
            Console.WriteLine($"{pad}  (PSO 포맷만 상세 파싱됩니다)");
            return;
        }

        // ── STRF 파일 경로 문자열 ─────────────────────────────────────────
        if (StrfStrings.Length > 0 && verbose)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  STRF 경로 문자열 ({StrfStrings.Length}개):");
            foreach (var s in StrfStrings.Take(20))
                Console.WriteLine($"{pad}    {s}");
            if (StrfStrings.Length > 20)
                Console.WriteLine($"{pad}    … ({StrfStrings.Length - 20}개 더)");
        }

        // ── MapDataGroups ─────────────────────────────────────────────────
        if (MapDataGroups.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  MapDataGroups ({MapDataGroups.Length}개 ymap 그룹):");
            foreach (var g in MapDataGroups)
                g.Print(pad + "  ", verbose);
        }

        // ── imapDependencies ──────────────────────────────────────────────
        if (ImapDependencies.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  imapDependencies ({ImapDependencies.Length}개 ymap→ytyp 링크):");
            foreach (var d in ImapDependencies.Take(verbose ? int.MaxValue : 10))
                d.Print(pad + "  ");
            if (!verbose && ImapDependencies.Length > 10)
                Console.WriteLine($"{pad}    … ({ImapDependencies.Length - 10}개 더)");
        }

        // ── imapDependencies_2 ────────────────────────────────────────────
        if (ImapDependencies2.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  imapDependencies_2 ({ImapDependencies2.Length}개):");
            foreach (var d in ImapDependencies2.Take(verbose ? int.MaxValue : 10))
                d.Print(pad + "  ", verbose);
            if (!verbose && ImapDependencies2.Length > 10)
                Console.WriteLine($"{pad}    … ({ImapDependencies2.Length - 10}개 더)");
        }

        // ── itypDependencies_2 ────────────────────────────────────────────
        if (ItypDependencies2.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  itypDependencies_2 ({ItypDependencies2.Length}개 ytyp):");
            foreach (var d in ItypDependencies2.Take(verbose ? int.MaxValue : 10))
                d.Print(pad + "  ", verbose);
            if (!verbose && ItypDependencies2.Length > 10)
                Console.WriteLine($"{pad}    … ({ItypDependencies2.Length - 10}개 더)");
        }

        // ── HDTxdBindings ─────────────────────────────────────────────────
        if (HdTxdBindings.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  HDTxdBindings ({HdTxdBindings.Length}개):");
            foreach (var b in HdTxdBindings)
                b.Print(pad + "  ");
        }

        // ── Interiors ─────────────────────────────────────────────────────
        if (Interiors.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  Interiors ({Interiors.Length}개):");
            foreach (var i in Interiors)
                i.Print(pad + "  ", verbose);
        }
    }

    public string Summary()
    {
        if (FormatType == "RBF")  return "[RBF — HDTxd 바인딩 전용]";
        if (FormatType != "PSO")  return "[미지원 포맷]";

        var parts = new List<string>();
        if (MapDataGroups.Length     > 0) parts.Add($"ymaps={MapDataGroups.Length}");
        if (HdTxdBindings.Length     > 0) parts.Add($"hdtxd={HdTxdBindings.Length}");
        if (ImapDependencies.Length  > 0) parts.Add($"imap_deps={ImapDependencies.Length}");
        if (ImapDependencies2.Length > 0) parts.Add($"imap_deps2={ImapDependencies2.Length}");
        if (ItypDependencies2.Length > 0) parts.Add($"ityp_deps2={ItypDependencies2.Length}");
        if (Interiors.Length         > 0) parts.Add($"interiors={Interiors.Length}");
        return parts.Count > 0 ? string.Join("  ", parts) : "(빈 매니페스트)";
    }
}


// =============================================================================
// ── 10. 진입점 ───────────────────────────────────────────────────────────────
// =============================================================================

class Program
{
    static void Main(string[] args)
    {
        string target  = @"E:\myGames-Resources\GTA5-DAT";
        bool   verbose = false;

        foreach (var a in args)
        {
            if (a == "--verbose" || a == "-v") verbose = true;
            else target = a;
        }

        string[] files = File.Exists(target)
            ? [target]
            : Directory.GetFiles(target, "_manifest.ymf", SearchOption.AllDirectories);

        Console.WriteLine($"_manifest.ymf 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        foreach (var path in files)
        {
            // 경로에서 RPF 이름 추출하여 표시
            string rpfLabel = Path.GetDirectoryName(path) is { } dir
                ? Path.GetFileName(dir)
                : path;

            Console.WriteLine($"{'═',1}{'═',-60} {rpfLabel}");
            try
            {
                var ymf = YmfFileInfo.Load(path);
                ok++;

                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  경로: {path}");
                    Console.WriteLine();
                    ymf.Print("  ", verbose);
                }
                else
                {
                    Console.WriteLine($"  {ymf.Summary()}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  [실패] {ex.Message}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"── 결과: 성공 {ok} / 전체 {files.Length} ──");
        Console.Read();
    }
}

} // namespace YmfReader
