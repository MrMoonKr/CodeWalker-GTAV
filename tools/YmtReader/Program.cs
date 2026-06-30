// =============================================================================
// YmtReader — GTA5 .ymt 파일 파서 (학습용, 독립 실행)
//
// .ymt 파일은 세 가지 포맷으로 존재합니다:
//
//  1. RSC Meta  — 시나리오 포인트 지역 (CScenarioPointRegion)
//                 magic: Unknown_10h == 0x50524430 ("PRD0")
//                 디렉토리 예시: x64w.rpf/scenarios/gta5/_hills/
//
//  2. RBF       — 부모 TXD 매핑 (CMapParentTxds)
//                 magic: "RBF0" (0x30464252 LE)
//                 파일 예시: update.rpf/common/data/parenttxds.ymt
//
//  3. PSO       — 시나리오 매니페스트 (CScenarioPointManifest)
//                 magic: "PSIN" (빅엔디안 첫 4바이트)
//                 PSO 포맷은 .ymf 와 동일한 구조
//
// 실행:  dotnet run -- "C:\path\to\file.ymt" [--verbose]
//        dotnet run -- "C:\path\to\dir" [--verbose]
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YmtReader
{

// =============================================================================
// ── 1. 포맷 감지 및 공통 유틸 ────────────────────────────────────────────────
// =============================================================================

enum YmtFormat { Unknown, RscMeta, Rbf, Pso }

// GTA5 RSC 가상 주소 (RSC Meta DataBlock 포인터 해석용)
static class VA
{
    const uint SYS = 0x50000000;
    const uint SEG = 0xF0000000;
    const uint OFF = 0x0FFFFFFF;
    static uint Lo32(ulong ptr) => (uint)ptr;
    public static bool IsSys(ulong ptr) => (Lo32(ptr) & SEG) == SYS && (Lo32(ptr) & OFF) != 0;
    public static int FileOffset(ulong ptr) => (int)(Lo32(ptr) & OFF);
}

// 파일 내용을 읽는 작은 래퍼
class R
{
    readonly byte[] _d;
    public R(byte[] data) => _d = data;
    public int    Len       => _d.Length;
    public byte   U8(int o) => o < _d.Length ? _d[o] : (byte)0;
    public ushort U16(int o) => o + 2 <= _d.Length ? BitConverter.ToUInt16(_d, o) : (ushort)0;
    public uint   U32(int o) => o + 4 <= _d.Length ? BitConverter.ToUInt32(_d, o) : 0u;
    public int    I32(int o) => o + 4 <= _d.Length ? BitConverter.ToInt32(_d, o) : 0;
    public ulong  U64(int o) => o + 8 <= _d.Length ? BitConverter.ToUInt64(_d, o) : 0ul;
    public float  F32(int o) => o + 4 <= _d.Length ? BitConverter.ToSingle(_d, o) : 0f;
    public string Str(int o, int max = 256)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < max && o + i < _d.Length && _d[o + i] != 0; i++)
            sb.Append((char)_d[o + i]);
        return sb.ToString();
    }
    // 슬라이스
    public byte[] Slice(int o, int len) {
        len = Math.Min(len, _d.Length - o);
        if (len <= 0) return [];
        var buf = new byte[len];
        Buffer.BlockCopy(_d, o, buf, 0, len);
        return buf;
    }
}

static class MagicHelper
{
    // RBF0  (little-endian: bytes 52 42 46 30 = 'R','B','F','0')
    public const uint RBF_MAGIC  = 0x30464252;
    // PSIN  (little-endian: bytes 50 53 49 4E = 'P','S','I','N')
    public const uint PSO_MAGIC  = 0x4E495350;
    // Meta RSC 마커: Unknown_10h == 0x50524430 ("PRD0")
    public const uint META_MAGIC = 0x50524430;

    public static YmtFormat Detect(R r)
    {
        uint magic0 = r.U32(0);
        if (magic0 == RBF_MAGIC)  return YmtFormat.Rbf;
        if (magic0 == PSO_MAGIC)  return YmtFormat.Pso;
        if (r.U32(0x10) == META_MAGIC) return YmtFormat.RscMeta;
        return YmtFormat.Unknown;
    }
}


// =============================================================================
// ── 2. RBF 파서 ──────────────────────────────────────────────────────────────
//
//  RBF (Raw Binary Format) 는 XML과 유사한 트리 구조를 바이너리로 인코딩합니다.
//
//  파일 레이아웃:
//    [0x00] u32  Magic = 0x30464252 ("RBF0")
//    이후 반복:
//      byte descriptorIndex
//        0xFF 0xFF : 닫기 태그
//        0xFD 0xFF : bytes 블롭 (i32 length + data)
//        else      : dataType 바이트
//          descriptorIndex == descriptors.Count → 새 descriptor
//            i16 nameLen + bytes name
//          데이터 타입:
//            0x00 = 구조체 열기 (i16×3 + PendingAttributes)
//            0x10 = u32
//            0x20 = bool=true
//            0x30 = bool=false
//            0x40 = float
//            0x50 = float3 (f32×3)
//            0x60 = string (i16 len + ASCII bytes)
// =============================================================================

abstract class RbfNode
{
    public string Name = "";
}

class RbfStruct : RbfNode
{
    public List<RbfNode> Children = new();
    public override string ToString() => $"<{Name}> ({Children.Count} children)";
}

class RbfBytesNode : RbfNode
{
    public byte[] Value = [];
    public override string ToString() => $"{Name}: [{Value.Length} bytes]";
}

class RbfUint32Node : RbfNode
{
    public uint Value;
    public override string ToString() => $"{Name}: {Value}";
}

class RbfBoolNode : RbfNode
{
    public bool Value;
    public override string ToString() => $"{Name}: {Value}";
}

class RbfFloatNode : RbfNode
{
    public float Value;
    public override string ToString() => $"{Name}: {Value:F4}";
}

class RbfFloat3Node : RbfNode
{
    public float X, Y, Z;
    public override string ToString() => $"{Name}: ({X:F3},{Y:F3},{Z:F3})";
}

class RbfStringNode : RbfNode
{
    public string Value = "";
    public override string ToString() => $"{Name}: \"{Value}\"";
}

class RbfParser
{
    readonly BinaryReader _r;
    readonly List<(string Name, byte Type)> _descs = new();
    RbfStruct? _cur;
    readonly Stack<RbfStruct> _stack = new();

    public RbfParser(byte[] data)
    {
        _r = new BinaryReader(new MemoryStream(data));
        uint magic = _r.ReadUInt32();
        if (magic != MagicHelper.RBF_MAGIC)
            throw new InvalidDataException($"RBF 마직 불일치: 0x{magic:X8}");
    }

    public RbfStruct Parse()
    {
        while (_r.BaseStream.Position < _r.BaseStream.Length)
        {
            byte descIdx = _r.ReadByte();

            if (descIdx == 0xFF)              // 닫기 태그
            {
                byte next = _r.ReadByte();
                if (next != 0xFF) throw new InvalidDataException("닫기 태그: 두 번째 바이트가 0xFF 아님");
                if (_stack.Count > 0)
                    _cur = _stack.Pop();
                else
                    return _cur ?? new RbfStruct();
                continue;
            }

            if (descIdx == 0xFD)              // bytes 블롭
            {
                byte next = _r.ReadByte();
                if (next != 0xFF) throw new InvalidDataException("bytes 블롭: 두 번째 바이트가 0xFF 아님");
                int len = _r.ReadInt32();
                byte[] data = _r.ReadBytes(len);
                var node = new RbfBytesNode { Name = "_bytes_", Value = data };
                _cur?.Children.Add(node);
                continue;
            }

            byte dataType = _r.ReadByte();

            // 새 descriptor 등록
            if (descIdx == _descs.Count)
            {
                short nameLen = _r.ReadInt16();
                string name = Encoding.ASCII.GetString(_r.ReadBytes(nameLen));
                _descs.Add((name, dataType));
            }

            string elemName = descIdx < _descs.Count ? _descs[descIdx].Name : $"desc_{descIdx}";
            ParseValue(elemName, dataType);
        }
        return _cur ?? new RbfStruct();
    }

    void ParseValue(string name, byte dataType)
    {
        switch (dataType)
        {
            case 0x00:   // 구조체 열기
            {
                _r.ReadInt16(); _r.ReadInt16();
                short pending = _r.ReadInt16();
                var s = new RbfStruct { Name = name };
                _cur?.Children.Add(s);
                if (_cur != null) _stack.Push(_cur);
                _cur = s;
                break;
            }
            case 0x10:   // uint32
                _cur?.Children.Add(new RbfUint32Node { Name = name, Value = _r.ReadUInt32() });
                break;
            case 0x20:   // bool true
                _cur?.Children.Add(new RbfBoolNode { Name = name, Value = true });
                break;
            case 0x30:   // bool false
                _cur?.Children.Add(new RbfBoolNode { Name = name, Value = false });
                break;
            case 0x40:   // float
                _cur?.Children.Add(new RbfFloatNode { Name = name, Value = _r.ReadSingle() });
                break;
            case 0x50:   // float3
                _cur?.Children.Add(new RbfFloat3Node
                {
                    Name = name,
                    X = _r.ReadSingle(), Y = _r.ReadSingle(), Z = _r.ReadSingle()
                });
                break;
            case 0x60:   // string
            {
                short len = _r.ReadInt16();
                string val = Encoding.ASCII.GetString(_r.ReadBytes(len));
                _cur?.Children.Add(new RbfStringNode { Name = name, Value = val });
                break;
            }
            default:
                throw new InvalidDataException($"알 수 없는 RBF 데이터 타입: 0x{dataType:X2} (필드={name})");
        }
    }
}


// =============================================================================
// ── 3. RSC Meta 파서 ─────────────────────────────────────────────────────────
//
//  RSC Meta 헤더 (0x70 = 112 bytes, file@0x00):
//
//   ResourceFileBase:
//    [0x00] u32  FileVft
//    [0x04] u32  Unknown4
//    [0x08] u64  FilePagesInfoPointer
//
//   Meta 헤더:
//    [0x10] i32  Unknown_10h         = 0x50524430 ("PRD0")
//    [0x14] i16  Unknown_14h         = 0x0079
//    [0x15] u8   HasEncryptedStrings
//    [0x16] u8   Unknown_17h
//    [0x18] i32  Unknown_18h
//    [0x1C] i32  RootBlockIndex      (1-based)
//    [0x20] i64  StructureInfosPointer → MetaStructureInfo[] (각 32 bytes)
//    [0x28] i64  EnumInfosPointer     → MetaEnumInfo[]
//    [0x30] i64  DataBlocksPointer    → MetaDataBlock[] (각 16 bytes)
//    [0x38] i64  NamePointer          → null-terminated string
//    [0x40] i64  EncryptedStringsPointer
//    [0x48] i16  StructureInfosCount
//    [0x4A] i16  EnumInfosCount
//    [0x4C] i16  DataBlocksCount
//    ...
//
//  MetaDataBlock (16 bytes):
//    [0x00] i32  StructureNameHash
//    [0x04] i32  DataLength
//    [0x08] i64  DataPointer
//
//  CScenarioPointRegion 내 주요 카운트 (raw bytes 직접 읽기):
//    [0x00] i32  VersionNumber
//    [0x08] Array_Structure LoadSavePoints → Count1 at [0x10]
//    [0x18] Array_Structure MyPoints      → Count1 at [0x20]
//    [0x48] Array_Structure EntityOverrides → Count1 at [0x50]
//    [0x108] Array_Structure Clusters     → Count1 at [0x110]
// =============================================================================

// 알려진 MetaName 해시 → 이름 테이블
static class MetaNames
{
    static readonly Dictionary<uint, string> _map = new()
    {
        [1492970064] = "CScenarioPointRegion",
        [2380938603] = "CScenarioPointContainer",
        [4103049490] = "CScenarioPoint",
        [750308016]  = "CScenarioPointCluster",
        [3019621867] = "CScenarioPointLookUps",
        [1425675487] = "CScenarioPointManifest",
        [1251976652] = "CScenarioPointRegionDef",
        [3383680063] = "CScenarioPointGroup",
        [4213733800] = "CScenarioEntityOverride",
        [871314709]  = "CScenarioChain",
        [3340683255] = "CScenarioChainingNode",
        [4255409560] = "CScenarioChainingEdge",
        [2935248897] = "CMapParentTxds",
        [702683191]  = "Points",
        [3587988394] = "Clusters",
        [4685037]    = "vPositionAndDirection",
    };

    public static string Get(uint hash) =>
        _map.TryGetValue(hash, out var name) ? name : $"0x{hash:X8}";
}

class MetaBlockInfo
{
    public uint   TypeHash;
    public string TypeName = "";
    public int    DataLength;
    public ulong  DataPointer;
    public byte[] RawData = [];
}

class MetaScenarioStats
{
    public int VersionNumber;
    public int LoadSavePointCount;
    public int MyPointCount;
    public int EntityOverrideCount;
    public int ClusterCount;
}

class MetaInfo
{
    public uint   FileVft;
    public int    RootBlockIndex;
    public short  StructureInfosCount;
    public short  EnumInfosCount;
    public short  DataBlocksCount;
    public string FileName = "";

    public List<MetaBlockInfo> Blocks = new();
    public MetaBlockInfo?      RootBlock;
    public MetaScenarioStats?  ScenarioStats;

    public static MetaInfo Parse(R r)
    {
        var m = new MetaInfo
        {
            FileVft             = r.U32(0x00),
            RootBlockIndex      = r.I32(0x1C),
            StructureInfosCount = (short)r.U16(0x48),
            EnumInfosCount      = (short)r.U16(0x4A),
            DataBlocksCount     = (short)r.U16(0x4C),
        };

        ulong namePtr = r.U64(0x38);
        if (VA.IsSys(namePtr))
            m.FileName = r.Str(VA.FileOffset(namePtr));

        ulong dbPtr = r.U64(0x30);
        if (!VA.IsSys(dbPtr) || m.DataBlocksCount <= 0) return m;

        int dbOff = VA.FileOffset(dbPtr);
        for (int i = 0; i < m.DataBlocksCount; i++)
        {
            int bo = dbOff + i * 16;
            uint  hash   = r.U32(bo + 0);
            int   dlen   = r.I32(bo + 4);
            ulong dptr   = r.U64(bo + 8);

            var block = new MetaBlockInfo
            {
                TypeHash    = hash,
                TypeName    = MetaNames.Get(hash),
                DataLength  = dlen,
                DataPointer = dptr,
            };

            if (VA.IsSys(dptr))
            {
                int dOff = VA.FileOffset(dptr);
                block.RawData = r.Slice(dOff, dlen);
            }

            m.Blocks.Add(block);
        }

        // 루트 블록 (1-based index)
        int rootIdx = m.RootBlockIndex - 1;
        if (rootIdx >= 0 && rootIdx < m.Blocks.Count)
        {
            m.RootBlock = m.Blocks[rootIdx];
            if (m.RootBlock.TypeHash == 1492970064) // CScenarioPointRegion
                m.ScenarioStats = ParseScenarioRegion(m.RootBlock.RawData);
        }

        return m;
    }

    // CScenarioPointRegion raw data에서 카운트 직접 읽기
    static MetaScenarioStats ParseScenarioRegion(byte[] d)
    {
        var s = new MetaScenarioStats();
        if (d.Length < 0x120) return s;
        s.VersionNumber      = BitConverter.ToInt32(d, 0x00);
        s.LoadSavePointCount = BitConverter.ToUInt16(d, 0x10);  // Points.LoadSavePoints.Count1
        s.MyPointCount       = BitConverter.ToUInt16(d, 0x20);  // Points.MyPoints.Count1
        s.EntityOverrideCount = BitConverter.ToUInt16(d, 0x50); // EntityOverrides.Count1
        s.ClusterCount       = BitConverter.ToUInt16(d, 0x110); // Clusters.Count1
        return s;
    }
}


// =============================================================================
// ── 4. PSO 파서 (간략) ───────────────────────────────────────────────────────
//
//  PSO 섹션 구조는 .ymf 와 동일합니다:
//   [0x00] u32  SectionTag  (PSIN / PMAP / PSCH / STRF / STRS / STRE / PSIG / CHKS)
//   [0x04] u32  Unknown
//   [0x08] u32  SectionSize
//   [0x0C] u32  Unknown
//
//  PMAP 섹션: 블록 맵
//   [+8] i32  RootId
//   [+12] i32 Count
//   [+16] entries (각 16 bytes: nameHash + offset + unknown + length)
// =============================================================================

class PsoSectionInfo
{
    public string Tag = "";
    public int    Size;
    public int    Offset;
}

class PsoBlockInfo
{
    public uint   NameHash;
    public string Name = "";
    public int    Offset;
    public int    Length;
}

class PsoInfo
{
    public List<PsoSectionInfo> Sections = new();
    public int    RootId;
    public List<PsoBlockInfo>  Blocks = new();

    static string TagName(uint tag)
    {
        byte[] b = BitConverter.GetBytes(tag);
        // BE로 저장된 태그를 ASCII로
        return new string(new char[] { (char)b[3], (char)b[2], (char)b[1], (char)b[0] });
    }

    public static PsoInfo Parse(R r)
    {
        var p = new PsoInfo();
        int pos = 0;
        while (pos + 16 <= r.Len)
        {
            uint tagLE = r.U32(pos);          // PSIN → 0x4E495350 LE
            // 태그를 BE 바이트 순서로 출력하기 위해 4바이트 뒤집기
            byte[] tb = BitConverter.GetBytes(tagLE);
            string tag = new string(new[] { (char)tb[3], (char)tb[2], (char)tb[1], (char)tb[0] });

            int secSize = (int)r.U32(pos + 8);
            p.Sections.Add(new PsoSectionInfo { Tag = tag, Size = secSize, Offset = pos });

            if (tag == "PMAP" && secSize >= 16)
            {
                int o = pos + 16;   // PMAP 데이터 시작
                p.RootId = r.I32(o + 8 - 16 + 8);  // secOff+8 = o
                // PMAP 레이아웃: [secStart+16] = 데이터
                // PMAP 헤더: secTag(4)+unk(4)+secSize(4)+unk(4) = 16 bytes
                // 이후 데이터: RootId(4) + Count(4) + Entries[count*16]
                int pmapData = pos + 16;
                int rootId = r.I32(pmapData);
                int count  = r.I32(pmapData + 4);
                p.RootId = rootId;
                for (int i = 0; i < count && pmapData + 8 + i * 16 + 16 <= r.Len; i++)
                {
                    int eo = pmapData + 8 + i * 16;
                    var blk = new PsoBlockInfo
                    {
                        NameHash = r.U32(eo),
                        Name     = MetaNames.Get(r.U32(eo)),
                        Offset   = r.I32(eo + 4),
                        Length   = r.I32(eo + 12),
                    };
                    p.Blocks.Add(blk);
                }
            }

            if (secSize <= 0) break;
            pos += 16 + secSize;
        }
        return p;
    }
}


// =============================================================================
// ── 5. YmtFileInfo — 파일 전체 파싱 결과 ─────────────────────────────────────
// =============================================================================

class YmtFileInfo
{
    public int       FileSize;
    public YmtFormat Format;
    public string    FormatName => Format switch
    {
        YmtFormat.RscMeta => "RSC Meta (CScenarioPointRegion 등)",
        YmtFormat.Rbf     => "RBF (CMapParentTxds 등)",
        YmtFormat.Pso     => "PSO (CScenarioPointManifest 등)",
        _                 => "알 수 없음",
    };

    // RBF 전용
    public RbfStruct? RbfRoot;
    public List<(string Parent, string Child)> TxdRelations = new();

    // RSC Meta 전용
    public MetaInfo? Meta;

    // PSO 전용
    public PsoInfo? Pso;

    public static YmtFileInfo Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r = new R(data);

        if (r.U32(0) == 0x37435352)
            throw new InvalidDataException("RSC7 압축 파일 — 압축 해제된 .ymt 만 지원합니다.");

        var f = new YmtFileInfo { FileSize = data.Length };
        f.Format = MagicHelper.Detect(r);

        switch (f.Format)
        {
            case YmtFormat.Rbf:
                f.RbfRoot = new RbfParser(data).Parse();
                f.TxdRelations = ExtractTxdRelations(f.RbfRoot);
                break;

            case YmtFormat.RscMeta:
                f.Meta = MetaInfo.Parse(r);
                break;

            case YmtFormat.Pso:
                f.Pso = PsoInfo.Parse(r);
                break;
        }

        return f;
    }

    // CMapParentTxds 에서 parent/child 쌍 추출
    static List<(string Parent, string Child)> ExtractTxdRelations(RbfStruct root)
    {
        var result = new List<(string, string)>();
        foreach (var child in root.Children)
        {
            if (child is RbfStruct s && s.Name == "txdRelationships")
            {
                foreach (var item in s.Children)
                {
                    if (item is RbfStruct itemStruct && itemStruct.Name == "item")
                    {
                        string parent = "", childTxd = "";
                        foreach (var f in itemStruct.Children)
                        {
                            if (f is RbfStruct fs)
                            {
                                string val = (fs.Children.FirstOrDefault() as RbfStringNode)?.Value ?? "";
                                if (fs.Name == "parent") parent = val;
                                else if (fs.Name == "child") childTxd = val;
                            }
                        }
                        if (parent.Length > 0 || childTxd.Length > 0)
                            result.Add((parent, childTxd));
                    }
                }
            }
        }
        return result;
    }

    public void Print(string pad, bool verbose)
    {
        Console.WriteLine($"{pad}  포맷: {FormatName}");

        switch (Format)
        {
            case YmtFormat.Rbf:     PrintRbf(pad, verbose); break;
            case YmtFormat.RscMeta: PrintMeta(pad, verbose); break;
            case YmtFormat.Pso:     PrintPso(pad, verbose); break;
            default:
                Console.WriteLine($"{pad}  [알 수 없는 포맷]");
                break;
        }
    }

    // ── RBF 출력 ─────────────────────────────────────────────────────────────
    void PrintRbf(string pad, bool verbose)
    {
        if (RbfRoot == null) return;
        Console.WriteLine($"{pad}  루트 노드: \"{RbfRoot.Name}\"  (자식={RbfRoot.Children.Count})");

        if (TxdRelations.Count > 0)
        {
            Console.WriteLine($"{pad}  TXD 관계: {TxdRelations.Count}개");
            if (verbose)
            {
                foreach (var (parent, child) in TxdRelations)
                    Console.WriteLine($"{pad}    {child,-40} → {parent}");
            }
            else
            {
                // 요약: 처음 5개만
                foreach (var (parent, child) in TxdRelations.Take(5))
                    Console.WriteLine($"{pad}    {child,-40} → {parent}");
                if (TxdRelations.Count > 5)
                    Console.WriteLine($"{pad}    ... 외 {TxdRelations.Count - 5}개");
            }
        }
        else if (verbose)
        {
            PrintRbfTree(pad + "  ", RbfRoot, 0, maxDepth: 4);
        }
    }

    void PrintRbfTree(string pad, RbfNode node, int depth, int maxDepth)
    {
        if (depth > maxDepth) { Console.WriteLine($"{pad}  ..."); return; }
        if (node is RbfStruct s)
        {
            Console.WriteLine($"{pad}  <{s.Name}>");
            foreach (var c in s.Children)
                PrintRbfTree(pad + "  ", c, depth + 1, maxDepth);
            Console.WriteLine($"{pad}  </{s.Name}>");
        }
        else
            Console.WriteLine($"{pad}  {node}");
    }

    // ── RSC Meta 출력 ────────────────────────────────────────────────────────
    void PrintMeta(string pad, bool verbose)
    {
        if (Meta == null) return;

        if (Meta.FileName.Length > 0)
            Console.WriteLine($"{pad}  파일명: \"{Meta.FileName}\"");
        Console.WriteLine($"{pad}  데이터 블록: {Meta.DataBlocksCount}개");
        Console.WriteLine($"{pad}  구조체 정보: {Meta.StructureInfosCount}개");
        Console.WriteLine($"{pad}  루트 블록 인덱스: {Meta.RootBlockIndex}");

        if (Meta.RootBlock != null)
        {
            Console.WriteLine($"{pad}  루트 블록 타입: {Meta.RootBlock.TypeName}  ({Meta.RootBlock.DataLength} bytes)");
        }

        if (Meta.ScenarioStats is { } ss)
        {
            Console.WriteLine($"{pad}  ── CScenarioPointRegion 통계:");
            Console.WriteLine($"{pad}    버전: {ss.VersionNumber}");
            Console.WriteLine($"{pad}    LoadSavePoints: {ss.LoadSavePointCount}개");
            Console.WriteLine($"{pad}    MyPoints (시나리오 포인트): {ss.MyPointCount}개");
            Console.WriteLine($"{pad}    EntityOverrides: {ss.EntityOverrideCount}개");
            Console.WriteLine($"{pad}    클러스터: {ss.ClusterCount}개");
        }

        if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine($"{pad}  ── 전체 데이터 블록 목록:");
            foreach (var b in Meta.Blocks)
                Console.WriteLine($"{pad}    {b.TypeName,-40}  {b.DataLength,6} bytes");
        }
    }

    // ── PSO 출력 ─────────────────────────────────────────────────────────────
    void PrintPso(string pad, bool verbose)
    {
        if (Pso == null) return;
        Console.WriteLine($"{pad}  섹션 수: {Pso.Sections.Count}");
        Console.WriteLine($"{pad}  PMAP RootId: {Pso.RootId}");
        Console.WriteLine($"{pad}  블록 수: {Pso.Blocks.Count}");

        if (Pso.Blocks.Count > 0)
        {
            string rootName = Pso.RootId >= 0 && Pso.RootId < Pso.Blocks.Count
                ? Pso.Blocks[Pso.RootId].Name : "?";
            Console.WriteLine($"{pad}  루트 블록 타입: {rootName}");
        }

        if (verbose)
        {
            Console.WriteLine($"{pad}  섹션:");
            foreach (var s in Pso.Sections)
                Console.WriteLine($"{pad}    {s.Tag}  ({s.Size} bytes @ 0x{s.Offset:X})");
            if (Pso.Blocks.Count > 0)
            {
                Console.WriteLine($"{pad}  PMAP 블록:");
                foreach (var b in Pso.Blocks)
                    Console.WriteLine($"{pad}    [{b.Offset:X6}] {b.Name,-36} {b.Length,6} bytes");
            }
        }
    }

    public string Summary()
    {
        return Format switch
        {
            YmtFormat.Rbf =>
                TxdRelations.Count > 0
                    ? $"RBF  CMapParentTxds  관계={TxdRelations.Count}"
                    : $"RBF  root=\"{RbfRoot?.Name}\"",
            YmtFormat.RscMeta =>
                Meta?.RootBlock != null
                    ? (Meta.ScenarioStats != null
                        ? $"RSC  {Meta.RootBlock.TypeName}  points={Meta.ScenarioStats.MyPointCount}  clusters={Meta.ScenarioStats.ClusterCount}"
                        : $"RSC  {Meta.RootBlock.TypeName}  blocks={Meta.DataBlocksCount}")
                    : $"RSC  blocks={Meta?.DataBlocksCount}",
            YmtFormat.Pso =>
                Pso?.Blocks.Count > 0
                    ? $"PSO  root={Pso.Blocks.FirstOrDefault(b => b.Offset == Pso.RootId)?.Name ?? "?"}  blocks={Pso.Blocks.Count}"
                    : $"PSO  sections={Pso?.Sections.Count}",
            _ => "알 수 없는 포맷",
        };
    }
}


// =============================================================================
// ── 6. 진입점 ────────────────────────────────────────────────────────────────
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
            : Directory.GetFiles(target, "*.ymt", SearchOption.AllDirectories);

        Console.WriteLine($".ymt 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"{'═',1}{'═',-60} {Path.GetFileName(path)}");
            try
            {
                var ymt = YmtFileInfo.Load(path);
                ok++;

                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  파일 크기: {ymt.FileSize:N0} bytes");
                    Console.WriteLine();
                    ymt.Print("  ", verbose);
                }
                else
                {
                    Console.WriteLine($"  {ymt.Summary()}");
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
        if (files.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine("힌트: .ymt 파일은 GTA5 RPF 아카이브 내부에 있습니다.");
            Console.WriteLine("  RSC Meta: x64w.rpf/scenarios/gta5/...");
            Console.WriteLine("  RBF:      update.rpf/common/data/parenttxds.ymt");
            Console.WriteLine("  PSO:      x64w.rpf/scenarios/gta5/.../manifest.ymt");
        }
        Console.Read();
    }
}

} // namespace YmtReader
