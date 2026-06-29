// =============================================================================
// YtypReader — GTA5 .ytyp 파일 파서 (학습용, 독립 실행)
//
// .ytyp 은 .ymap 과 같은 Meta 포맷입니다.
// 루트 블록 타입이 CMapData 대신 CMapTypes 인 것만 다릅니다.
//
// 포인터 체계 (YmapReader 와 동일):
//   ① VA (Virtual Address)  — 파일 헤더의 포인터
//        0x5??????? → 시스템 세그먼트, 파일 오프셋 = ptr & 0x0FFFFFFF
//   ② MetaPtr (Block + Offset 인코딩) — 데이터 블록 내부 포인터
//        u64 하위 12비트 = BlockId (1-based, 0 = null)
//        u64 bits[12..31] = 해당 블록 내 byte 오프셋
//
// 아키타입 종류 세 가지:
//   CBaseArchetypeDef  (144 bytes) — 일반 오브젝트
//   CTimeArchetypeDef  (160 bytes) — = CBaseArchetypeDef + timeFlags (특정 시간대만 표시)
//   CMloArchetypeDef   (240 bytes) — = CBaseArchetypeDef + MLO 내부 공간 데이터
//
// 실행:  dotnet run -- "폴더경로"
//        dotnet run -- "파일.ytyp" --verbose
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace YtypReader
{

// =============================================================================
// ── 1. VA — 파일 헤더에서 사용하는 가상 주소 → 파일 오프셋 변환
// =============================================================================

static class VA
{
    const uint SYS = 0x50000000;
    const uint GFX = 0x60000000;
    const uint SEG = 0xF0000000;
    const uint OFF = 0x0FFFFFFF;

    static uint Lo32(ulong ptr) => (uint)ptr;

    public static bool IsValid(ulong ptr) =>
        (Lo32(ptr) & OFF) != 0 &&
        ((Lo32(ptr) & SEG) == SYS || (Lo32(ptr) & SEG) == GFX);

    public static int FileOffset(ulong ptr) => (int)(Lo32(ptr) & OFF);

    public static string Describe(ulong ptr) =>
        IsValid(ptr) ? $"0x{ptr:X8}  →  file@0x{FileOffset(ptr):X}" : "(null)";
}


// =============================================================================
// ── 2. ResourceReader — byte[] 래퍼
// =============================================================================

class ResourceReader
{
    readonly byte[] _data;

    public ResourceReader(byte[] data) => _data = data;

    public int    Length       => _data.Length;
    public byte   U8 (int off) => _data[off];
    public ushort U16(int off) => BitConverter.ToUInt16(_data, off);
    public uint   U32(int off) => BitConverter.ToUInt32(_data, off);
    public ulong  U64(int off) => BitConverter.ToUInt64(_data, off);
    public float  F32(int off) => BitConverter.ToSingle(_data, off);

    public (float X, float Y, float Z) F32x3(int off) =>
        (F32(off), F32(off + 4), F32(off + 8));
}


// =============================================================================
// ── 3. MetaPtr — 데이터 블록 내부 포인터 인코딩
// =============================================================================

readonly struct MetaPtr
{
    readonly ulong _raw;

    public MetaPtr(ulong raw)   => _raw = raw;
    public bool   IsNull        => (_raw & 0xFFF) == 0;
    public int    BlockId       => (int)(_raw & 0xFFF);  // 1-based
    public int    BlockIndex    => BlockId - 1;          // 0-based
    public int    Offset        => (int)((_raw >> 12) & 0xFFFFF);

    public override string ToString() =>
        IsNull ? "(null)" : $"block[{BlockId}] + 0x{Offset:X}";
}


// =============================================================================
// ── 4. MetaArray — 16 bytes, 데이터 블록 내 배열 서술자
//
//  [0x00] u64  Pointer  — MetaPtr 인코딩
//  [0x08] u16  Count1
//  [0x0A] u16  Count2
//  [0x0C] u32  Reserved
//
//  Array_StructurePointer: 참조 블록에 MetaPtr[] (간접 참조)
//  Array_Structure:        참조 블록에 구조체 직접 나열 (직접 참조)
// =============================================================================

readonly struct MetaArray
{
    public readonly MetaPtr Ptr;
    public readonly ushort  Count1;
    public readonly ushort  Count2;

    public MetaArray(ulong raw, ushort c1, ushort c2)
    {
        Ptr    = new MetaPtr(raw);
        Count1 = c1;
        Count2 = c2;
    }

    public bool IsEmpty => Ptr.IsNull || Count1 == 0;
}


// =============================================================================
// ── 5. MetaHeader — 파일 헤더, 0x70 bytes  (YmapReader 와 동일)
// =============================================================================

class MetaHeader
{
    public uint   VftLo;
    public ulong  PagesInfoPtr;
    public uint   Magic;
    public ushort Version;
    public uint   RootBlockIndex;
    public ulong  StructInfosPtr;
    public ulong  EnumInfosPtr;
    public ulong  DataBlocksPtr;
    public ulong  NamePtr;
    public ushort StructInfosCount;
    public ushort EnumInfosCount;
    public ushort DataBlocksCount;

    public static MetaHeader Read(ResourceReader r)
    {
        return new MetaHeader
        {
            VftLo            = r.U32(0x00),
            PagesInfoPtr     = r.U64(0x08),
            Magic            = r.U32(0x10),
            Version          = r.U16(0x14),
            RootBlockIndex   = r.U32(0x1C),
            StructInfosPtr   = r.U64(0x20),
            EnumInfosPtr     = r.U64(0x28),
            DataBlocksPtr    = r.U64(0x30),
            NamePtr          = r.U64(0x38),
            StructInfosCount = r.U16(0x48),
            EnumInfosCount   = r.U16(0x4A),
            DataBlocksCount  = r.U16(0x4C),
        };
    }

    public bool IsMeta => Magic == 0x50524430; // "PRD0"
    public bool IsRsc7 => Magic == 0x37435352; // "RSC7"

    public void Print(bool verbose)
    {
        Out.Log("  [헤더]", "Magic",          $"0x{Magic:X8}  ({(IsMeta ? "PRD0 ✓" : "unknown")})");
        Out.Log("  [헤더]", "Version",        $"0x{Version:X4}");
        Out.Log("  [헤더]", "RootBlockIndex", $"{RootBlockIndex}  (1-based)");
        Out.Log("  [헤더]", "DataBlocksPtr",  VA.Describe(DataBlocksPtr));
        Out.Log("  [헤더]", "DataBlocksCount",$"{DataBlocksCount}");
        if (verbose)
        {
            Out.Log("  [헤더]", "VftLo",           $"0x{VftLo:X8}");
            Out.Log("  [헤더]", "PagesInfoPtr",     VA.Describe(PagesInfoPtr));
            Out.Log("  [헤더]", "StructInfosCount", $"{StructInfosCount}");
            Out.Log("  [헤더]", "EnumInfosCount",   $"{EnumInfosCount}");
            Out.Log("  [헤더]", "StructInfosPtr",   VA.Describe(StructInfosPtr));
            Out.Log("  [헤더]", "EnumInfosPtr",     VA.Describe(EnumInfosPtr));
        }
    }
}


// =============================================================================
// ── 6. MetaDataBlock — 16 bytes
//
//   [0x00] u32  StructureNameHash  — 데이터 타입 식별자
//   [0x04] u32  DataLength
//   [0x08] u64  DataPointer        — VA: 실제 데이터 위치
//
//  주요 StructureNameHash (MetaName 값):
//    0xD97CE1A1 (3648877985)  = CMapTypes         ← .ytyp 루트 블록
//    0x82FEF1E5 (2198872549)  = CBaseArchetypeDef
//    0x76BF9F1C (1995403036)  = CTimeArchetypeDef
//    0x104F1D3D ( 274759997)  = CMloArchetypeDef
//    0x6E2B7A63 (1847570019)  = POINTER[]          ← MetaPtr 배열 블록
// =============================================================================

class MetaDataBlock
{
    // MetaName 해시 상수 (MetaNames.cs 기준)
    public const uint NAME_CMapTypes          = 3649811809u; // 0xD97CE1A1
    public const uint NAME_CBaseArchetypeDef  = 2195127427u; // 0x82FEF1E5
    public const uint NAME_CTimeArchetypeDef  = 1991296364u; // 0x76BF9F1C
    public const uint NAME_CMloArchetypeDef   =  273704021u; // 0x104F1D3D
    public const uint NAME_POINTER            = 1847570019u; // 0x6E2B7A63

    public uint  NameHash;
    public uint  DataLength;
    public ulong DataPointer;
    public int   FileDataOffset;

    public static MetaDataBlock Read(ResourceReader r, int off)
    {
        ulong ptr = r.U64(off + 0x08);
        return new MetaDataBlock
        {
            NameHash       = r.U32(off + 0x00),
            DataLength     = r.U32(off + 0x04),
            DataPointer    = ptr,
            FileDataOffset = VA.IsValid(ptr) ? VA.FileOffset(ptr) : 0,
        };
    }

    public string TypeLabel => NameHash switch
    {
        NAME_CMapTypes         => "CMapTypes",
        NAME_CBaseArchetypeDef => "CBaseArchetypeDef",
        NAME_CTimeArchetypeDef => "CTimeArchetypeDef",
        NAME_CMloArchetypeDef  => "CMloArchetypeDef",
        NAME_POINTER           => "POINTER[]",
        _                      => $"0x{NameHash:X8}",
    };
}


// =============================================================================
// ── 7. Meta — 헤더 + DataBlocks 집합
// =============================================================================

class Meta
{
    public MetaHeader      Header = null!;
    public MetaDataBlock[] Blocks = [];

    public int ResolveMetaPtr(MetaPtr ptr)
    {
        if (ptr.IsNull) return -1;
        int idx = ptr.BlockIndex;
        if (idx < 0 || idx >= Blocks.Length) return -1;
        int fileBase = Blocks[idx].FileDataOffset;
        if (fileBase == 0) return -1;
        return fileBase + ptr.Offset;
    }

    public static Meta Read(ResourceReader r)
    {
        var hdr    = MetaHeader.Read(r);
        var blocks = new MetaDataBlock[hdr.DataBlocksCount];

        if (VA.IsValid(hdr.DataBlocksPtr))
        {
            int arrBase = VA.FileOffset(hdr.DataBlocksPtr);
            for (int i = 0; i < hdr.DataBlocksCount; i++)
                blocks[i] = MetaDataBlock.Read(r, arrBase + i * 16);
        }

        return new Meta { Header = hdr, Blocks = blocks };
    }
}


// =============================================================================
// ── 8. CMapTypes — .ytyp 루트 블록 (80 bytes)
//
//  .ymap 의 CMapData 에 해당하는 루트입니다.
//
//  Binary layout:
//   [  0] u32  Unused
//   [  8] MetaArray extensions           — Array_StructurePointer
//   [ 24] MetaArray archetypes           — Array_StructurePointer  ← 핵심
//   [ 40] u32  name                      — 타입맵 이름 해시
//   [ 48] MetaArray dependencies         — Array_uint (의존 리소스 해시)
//   [ 64] MetaArray compositeEntityTypes — Array_StructurePointer
// =============================================================================

class CMapTypes
{
    public int  FileOffset;
    public uint Name;
    public MetaArray Archetypes;     // 간접 참조 (Array_StructurePointer)
    public MetaArray Extensions;
    public MetaArray Dependencies;
    public MetaArray CompositeEntityTypes;

    public static CMapTypes Read(ResourceReader r, int off)
    {
        return new CMapTypes
        {
            FileOffset           = off,
            Extensions           = ReadArr(r, off +  8),
            Archetypes           = ReadArr(r, off + 24),
            Name                 = r.U32(off + 40),
            Dependencies         = ReadArr(r, off + 48),
            CompositeEntityTypes = ReadArr(r, off + 64),
        };
    }

    static MetaArray ReadArr(ResourceReader r, int off) =>
        new MetaArray(r.U64(off), r.U16(off + 8), r.U16(off + 10));

    public void Print(bool verbose)
    {
        Out.Log("  [CMapTypes]", "file offset",  $"0x{FileOffset:X}  (80 bytes)");
        Out.Log("  [CMapTypes]", "name",          $"0x{Name:X8}");
        Out.Log("  [CMapTypes]", "archetypes",    ArrDesc(Archetypes));
        if (verbose)
        {
            Out.Log("  [CMapTypes]", "extensions",  ArrDesc(Extensions));
            Out.Log("  [CMapTypes]", "dependencies",ArrDesc(Dependencies));
            Out.Log("  [CMapTypes]", "compositeTypes",ArrDesc(CompositeEntityTypes));
        }
    }

    static string ArrDesc(MetaArray a) =>
        a.IsEmpty ? "(empty)" : $"count={a.Count1}  ptr={a.Ptr}";
}


// =============================================================================
// ── 9. 아키타입 타입 (ArchetypeKind enum)
// =============================================================================

enum ArchetypeKind
{
    Base,   // CBaseArchetypeDef  (144 bytes)
    Time,   // CTimeArchetypeDef  (160 bytes) = Base + timeFlags
    Mlo,    // CMloArchetypeDef   (240 bytes) = Base + MLO 데이터
}


// =============================================================================
// ── 10. CBaseArchetypeDef — 아키타입 공통 필드 (144 bytes)
//
//  모든 아키타입 타입이 공유하는 앞 144 bytes 입니다.
//  CTimeArchetypeDef, CMloArchetypeDef 도 이 필드를 포함합니다.
//
//  Binary layout:
//   [  0] u32  Unused x2
//   [  8] f32  lodDist              — 이 LOD 가 사라지는 거리
//   [ 12] u32  flags
//   [ 16] u32  specialAttribute
//   [ 20] u32  Unused
//   [ 32] f32x3 bbMin               — 바운딩 박스 최솟값
//   [ 48] f32x3 bbMax               — 바운딩 박스 최댓값
//   [ 64] f32x3 bsCentre            — 바운딩 스피어 중심
//   [ 80] f32  bsRadius             — 바운딩 스피어 반지름
//   [ 84] f32  hdTextureDist        — HD 텍스처 로딩 거리
//   [ 88] u32  name                 — 아키타입 이름 해시 (JenkHash)
//   [ 92] u32  textureDictionary    — 텍스처 딕셔너리 해시
//   [ 96] u32  clipDictionary
//   [100] u32  drawableDictionary
//   [104] u32  physicsDictionary
//   [108] u32  assetType            — 0=Uninit 1=Fragment 2=Drawable 4=DrawableDict
//   [112] u32  assetName            — 에셋 파일 이름 해시
//   [120] MetaArray extensions      — 확장 데이터 (Array_StructurePointer)
// =============================================================================

class CBaseArchetypeDef
{
    public int  FileOffset;
    public ArchetypeKind Kind;

    public float LodDist;
    public uint  Flags;
    public uint  SpecialAttribute;
    public (float X, float Y, float Z) BbMin;
    public (float X, float Y, float Z) BbMax;
    public (float X, float Y, float Z) BsCentre;
    public float BsRadius;
    public float HdTextureDist;
    public uint  Name;
    public uint  TextureDictionary;
    public uint  ClipDictionary;
    public uint  DrawableDictionary;
    public uint  PhysicsDictionary;
    public uint  AssetType;
    public uint  AssetName;
    public MetaArray Extensions;

    // CTimeArchetypeDef 전용 (offset 144)
    public uint  TimeFlags;

    // CMloArchetypeDef 전용 (offset 144~)
    public uint      MloFlags;
    public MetaArray MloEntities;      // 인테리어 엔티티 (간접)
    public MetaArray MloRooms;         // 룸 (직접)
    public MetaArray MloPortals;       // 포털 (직접)
    public MetaArray MloEntitySets;    // 엔티티 세트 (직접)
    public MetaArray MloTimeCycleMods; // 타임사이클 수정자 (직접)

    public static CBaseArchetypeDef Read(ResourceReader r, int off, ArchetypeKind kind)
    {
        var a = new CBaseArchetypeDef
        {
            FileOffset        = off,
            Kind              = kind,
            LodDist           = r.F32(off +   8),
            Flags             = r.U32(off +  12),
            SpecialAttribute  = r.U32(off +  16),
            BbMin             = r.F32x3(off + 32),
            BbMax             = r.F32x3(off + 48),
            BsCentre          = r.F32x3(off + 64),
            BsRadius          = r.F32(off +  80),
            HdTextureDist     = r.F32(off +  84),
            Name              = r.U32(off +  88),
            TextureDictionary = r.U32(off +  92),
            ClipDictionary    = r.U32(off +  96),
            DrawableDictionary= r.U32(off + 100),
            PhysicsDictionary = r.U32(off + 104),
            AssetType         = r.U32(off + 108),
            AssetName         = r.U32(off + 112),
            Extensions        = ReadArr(r, off + 120),
        };

        if (kind == ArchetypeKind.Time)
        {
            // CTimeArchetypeDef: CBaseArchetypeDef (144 bytes) + u32 timeFlags
            a.TimeFlags = r.U32(off + 144);
        }
        else if (kind == ArchetypeKind.Mlo)
        {
            // CMloArchetypeDef: CBaseArchetypeDef (144 bytes) + MLO 데이터
            a.MloFlags        = r.U32(off + 144);
            a.MloEntities     = ReadArr(r, off + 152); // Array_StructurePointer
            a.MloRooms        = ReadArr(r, off + 168); // Array_Structure
            a.MloPortals      = ReadArr(r, off + 184); // Array_Structure
            a.MloEntitySets   = ReadArr(r, off + 200); // Array_Structure
            a.MloTimeCycleMods= ReadArr(r, off + 216); // Array_Structure
        }

        return a;
    }

    static MetaArray ReadArr(ResourceReader r, int off) =>
        new MetaArray(r.U64(off), r.U16(off + 8), r.U16(off + 10));

    // 에셋 타입 → 레이블 (파일에는 정수값 0-4 로 저장됨)
    public string AssetTypeLabel => AssetType switch
    {
        0 => "Uninit",
        1 => "Fragment",
        2 => "Drawable",
        3 => "DrawableDict",
        4 => "Assetless",
        _ => $"({AssetType})",
    };

    public string KindLabel => Kind switch
    {
        ArchetypeKind.Base => "Base",
        ArchetypeKind.Time => "Time",
        ArchetypeKind.Mlo  => "MLO",
        _                  => "?",
    };

    // timeFlags 에서 활성화된 시간대 문자열 생성 (bit 0 = 자정, bit 1 = 1시 ...)
    // timeFlags 는 u32이지만 실제로는 하위 24비트만 사용
    public string TimeFlagsHours()
    {
        if (Kind != ArchetypeKind.Time) return "";
        var on  = new List<string>();
        var off = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            if (((TimeFlags >> h) & 1) == 1) on.Add($"{h:D2}");
            else off.Add($"{h:D2}");
        }
        if (on.Count == 0)  return "항상 숨김";
        if (on.Count == 24) return "항상 표시";
        return $"표시시간=[{string.Join(",", on)}]";
    }

    public string Summary() =>
        Kind == ArchetypeKind.Time
            ? $"[{KindLabel,-4}] 0x{Name:X8}  lod={LodDist:F0}m  " +
              $"asset={AssetTypeLabel,-12}  {TimeFlagsHours()}"
            : Kind == ArchetypeKind.Mlo
            ? $"[{KindLabel,-4}] 0x{Name:X8}  lod={LodDist:F0}m  " +
              $"asset={AssetTypeLabel,-12}  " +
              $"rooms={MloRooms.Count1}  portals={MloPortals.Count1}"
            : $"[{KindLabel,-4}] 0x{Name:X8}  lod={LodDist:F0}m  " +
              $"asset={AssetTypeLabel,-12}";

    public void Print()
    {
        Out.Log("    ", "file offset",      $"0x{FileOffset:X}");
        Out.Log("    ", "name",             $"0x{Name:X8}");
        Out.Log("    ", "lodDist",          $"{LodDist:F1}m");
        Out.Log("    ", "flags",            $"0x{Flags:X8}");
        Out.Log("    ", "assetType",        AssetTypeLabel);
        Out.Log("    ", "assetName",        $"0x{AssetName:X8}");
        Out.Log("    ", "textureDictionary",$"0x{TextureDictionary:X8}");
        Out.Log("    ", "drawableDictionary",$"0x{DrawableDictionary:X8}");
        Out.Log("    ", "physicsDictionary",$"0x{PhysicsDictionary:X8}");
        Out.Log("    ", "bbMin",            Out.Vec3(BbMin));
        Out.Log("    ", "bbMax",            Out.Vec3(BbMax));
        Out.Log("    ", "bsCentre",         Out.Vec3(BsCentre));
        Out.Log("    ", "bsRadius",         $"{BsRadius:F3}");
        Out.Log("    ", "hdTextureDist",    $"{HdTextureDist:F1}m");
        if (Kind == ArchetypeKind.Time)
        {
            Out.Log("    ", "timeFlags",    $"0x{TimeFlags:X6}  {TimeFlagsHours()}");
        }
        else if (Kind == ArchetypeKind.Mlo)
        {
            Out.Log("    ", "mloFlags",     $"0x{MloFlags:X8}");
            Out.Log("    ", "entities",     ArrDesc(MloEntities));
            Out.Log("    ", "rooms",        ArrDesc(MloRooms));
            Out.Log("    ", "portals",      ArrDesc(MloPortals));
            Out.Log("    ", "entitySets",   ArrDesc(MloEntitySets));
        }
    }

    static string ArrDesc(MetaArray a) =>
        a.IsEmpty ? "(empty)" : $"count={a.Count1}  ptr={a.Ptr}";
}


// =============================================================================
// ── 11. YtypFile — 파일 로더
//
//  파싱 흐름:
//    ① MetaHeader 읽기 (0x70 bytes)
//       → Magic 검증 ("PRD0")
//
//    ② DataBlocks 배열 읽기
//       → 각 항목의 NameHash 로 타입 식별
//
//    ③ RootBlock = DataBlocks[RootBlockIndex - 1] → CMapTypes
//
//    ④ CMapTypes.archetypes 읽기 (간접 참조 2단계)
//       archetypes.Ptr → POINTER[] 블록 → 각 MetaPtr → 아키타입 블록
//       ★ 각 MetaPtr 가 가리키는 블록의 NameHash 로 아키타입 종류 결정
//          (CBaseArchetypeDef / CTimeArchetypeDef / CMloArchetypeDef)
// =============================================================================

class YtypFile
{
    public string              FilePath   = "";
    public int                 FileSize;
    public Meta                MetaInfo   = null!;
    public CMapTypes           MapTypes   = null!;
    public CBaseArchetypeDef[] Archetypes = [];

    public static YtypFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r    = new ResourceReader(data);
        var ytyp = new YtypFile { FilePath = path, FileSize = data.Length };

        // ── ① 헤더 읽기 ──────────────────────────────────────────────────
        var hdr = MetaHeader.Read(r);
        if (hdr.IsRsc7)
            throw new InvalidDataException("RSC7 압축 파일 — 압축 해제 후 로딩하세요.");
        if (!hdr.IsMeta)
            throw new InvalidDataException($"Meta 매직이 아닙니다: 0x{hdr.Magic:X8}");

        // ── ② DataBlocks 배열 읽기 ───────────────────────────────────────
        var meta = Meta.Read(r);
        ytyp.MetaInfo = meta;

        // ── ③ 루트 블록 = CMapTypes ─────────────────────────────────────
        int rootIdx = (int)hdr.RootBlockIndex - 1;
        if (rootIdx < 0 || rootIdx >= meta.Blocks.Length)
            throw new InvalidDataException(
                $"RootBlockIndex({hdr.RootBlockIndex}) 가 범위를 벗어남 (blocks={meta.Blocks.Length}).");

        var rootBlock = meta.Blocks[rootIdx];
        ytyp.MapTypes = CMapTypes.Read(r, rootBlock.FileDataOffset);

        // ── ④ Archetypes 읽기 (간접 참조 2단계) ─────────────────────────
        ytyp.Archetypes = ReadArchetypes(r, meta, ytyp.MapTypes.Archetypes);

        return ytyp;
    }

    // 간접 참조:
    //   archetypes.Ptr → POINTER[] 블록 (MetaPtr 배열)
    //   각 MetaPtr → 아키타입 블록 (CBaseArchetypeDef / CTimeArchetypeDef / CMloArchetypeDef)
    //   블록 NameHash 로 종류 결정
    static CBaseArchetypeDef[] ReadArchetypes(ResourceReader r, Meta meta, MetaArray arr)
    {
        if (arr.IsEmpty) return [];

        int ptrArrayOff = meta.ResolveMetaPtr(arr.Ptr);
        if (ptrArrayOff < 0) return [];

        var list = new List<CBaseArchetypeDef>(arr.Count1);
        for (int i = 0; i < arr.Count1; i++)
        {
            ulong raw    = r.U64(ptrArrayOff + i * 8);
            var   ptr    = new MetaPtr(raw);
            int   off    = meta.ResolveMetaPtr(ptr);
            if (off < 0) continue;

            // 블록 NameHash 로 아키타입 종류 결정
            ArchetypeKind kind = ArchetypeKind.Base;
            if (!ptr.IsNull && ptr.BlockIndex >= 0 && ptr.BlockIndex < meta.Blocks.Length)
            {
                uint blockName = meta.Blocks[ptr.BlockIndex].NameHash;
                kind = blockName switch
                {
                    MetaDataBlock.NAME_CTimeArchetypeDef => ArchetypeKind.Time,
                    MetaDataBlock.NAME_CMloArchetypeDef  => ArchetypeKind.Mlo,
                    _                                    => ArchetypeKind.Base,
                };
            }

            list.Add(CBaseArchetypeDef.Read(r, off, kind));
        }
        return list.ToArray();
    }
}


// =============================================================================
// ── 12. 출력 헬퍼
// =============================================================================

static class Out
{
    public static void Log(string prefix, string field, string value) =>
        Console.WriteLine($"{prefix}  {field,-28} {value}");

    public static string Vec3((float X, float Y, float Z) v) =>
        $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}


// =============================================================================
// ── 13. 진입점
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
            : Directory.GetFiles(target, "*.ytyp", SearchOption.AllDirectories);

        Console.WriteLine($".ytyp 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        int totalBase = 0, totalTime = 0, totalMlo = 0;

        foreach (var path in files)
        {
            Console.WriteLine($"══ {Path.GetFileName(path)}");
            try
            {
                var ytyp = YtypFile.Load(path);
                ok++;

                int baseCount = 0, timeCount = 0, mloCount = 0;
                foreach (var a in ytyp.Archetypes)
                {
                    if      (a.Kind == ArchetypeKind.Time) timeCount++;
                    else if (a.Kind == ArchetypeKind.Mlo)  mloCount++;
                    else                                   baseCount++;
                }
                totalBase += baseCount;
                totalTime += timeCount;
                totalMlo  += mloCount;

                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  파일 크기: {ytyp.FileSize:N0} bytes");
                    Console.WriteLine();

                    ytyp.MetaInfo.Header.Print(verbose);
                    Console.WriteLine();

                    Console.WriteLine("  [DataBlocks]");
                    for (int i = 0; i < ytyp.MetaInfo.Blocks.Length; i++)
                    {
                        var b = ytyp.MetaInfo.Blocks[i];
                        Console.WriteLine(
                            $"    [{i+1:D2}] {b.TypeLabel,-20}  " +
                            $"len={b.DataLength,6}  " +
                            $"data={VA.Describe(b.DataPointer)}");
                    }
                    Console.WriteLine();

                    ytyp.MapTypes.Print(verbose);
                    Console.WriteLine();

                    Console.WriteLine(
                        $"  [Archetypes]  " +
                        $"({ytyp.Archetypes.Length}개:  " +
                        $"Base={baseCount}  Time={timeCount}  MLO={mloCount})");

                    int show = verbose
                        ? ytyp.Archetypes.Length
                        : Math.Min(ytyp.Archetypes.Length, 15);

                    for (int i = 0; i < show; i++)
                    {
                        Console.WriteLine($"    [{i:D3}] {ytyp.Archetypes[i].Summary()}");
                        if (verbose) ytyp.Archetypes[i].Print();
                    }
                    if (show < ytyp.Archetypes.Length)
                        Console.WriteLine(
                            $"    ... 이하 {ytyp.Archetypes.Length - show}개 생략" +
                            $" (--verbose 로 전체 출력)");
                    Console.WriteLine();
                }
                else
                {
                    // 폴더 요약 모드
                    Console.WriteLine(
                        $"  name=0x{ytyp.MapTypes.Name:X8}  " +
                        $"archetypes={ytyp.Archetypes.Length}" +
                        (baseCount > 0 ? $"  Base={baseCount}" : "") +
                        (timeCount > 0 ? $"  Time={timeCount}" : "") +
                        (mloCount  > 0 ? $"  MLO={mloCount}"  : "") +
                        $"  blocks={ytyp.MetaInfo.Blocks.Length}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [실패] {ex.Message}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Console.WriteLine(
            $"── 결과: 성공 {ok} / 실패 {fail} / 전체 {files.Length} ──");
        if (ok > 1)
            Console.WriteLine(
                $"── 아키타입 합계: Base={totalBase}  Time={totalTime}  MLO={totalMlo}" +
                $"  (total={totalBase+totalTime+totalMlo}) ──");

        Console.Write( "계속하려면 아무키나 누르세요 : " ) ;
        Console.Read() ;
    }
}

} // namespace YtypReader
