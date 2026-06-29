// =============================================================================
// YmapReader — GTA5 .ymap 파일 파서 (학습용, 독립 실행)
//
// .ymap 은 .ybn 과 달리 "Meta 포맷"을 사용합니다.
// 두 종류의 포인터 체계를 동시에 사용하는 것이 핵심입니다:
//
//   ① VA (Virtual Address) — 파일 헤더의 포인터
//        0x5??????? → 시스템 세그먼트, 파일 오프셋 = ptr & 0x0FFFFFFF
//
//   ② MetaPtr (Block + Offset 인코딩) — 데이터 블록 내부 포인터
//        u64 하위 12비트 = BlockId (1-based, 0 = null)
//        u64 bits[12..31] = 해당 블록 내 byte 오프셋
//
// 실행:  dotnet run -- "폴더경로"
//        dotnet run -- "파일.ymap" --verbose
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace YmapReader
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

    public (float X, float Y, float Z, float W) F32x4(int off) =>
        (F32(off), F32(off + 4), F32(off + 8), F32(off + 12));
}


// =============================================================================
// ── 3. MetaPtr — 데이터 블록 내부 포인터 인코딩
//
//  .ymap 데이터 블록 안의 포인터는 VA(0x5???????) 가 아니라
//  (BlockId, Offset) 인코딩입니다:
//
//    u64 하위 12비트 = BlockId  (1-based, 0 = null)
//    u64 bits[12..31] = 해당 블록 내 byte 오프셋
//
//  MetaArray 의 Pointer 필드도 같은 인코딩을 사용합니다.
// =============================================================================

readonly struct MetaPtr
{
    readonly ulong _raw;

    public MetaPtr(ulong raw)   => _raw = raw;
    public bool   IsNull        => (_raw & 0xFFF) == 0;
    public int    BlockId       => (int)(_raw & 0xFFF);      // 1-based
    public int    BlockIndex    => BlockId - 1;              // 0-based
    public int    Offset        => (int)((_raw >> 12) & 0xFFFFF);

    public override string ToString() =>
        IsNull ? "(null)" : $"block[{BlockId}] + 0x{Offset:X}";
}


// =============================================================================
// ── 4. MetaArray — 16 bytes, 데이터 블록 내 배열 서술자
//
//  두 종류가 있지만 바이너리 레이아웃은 동일합니다:
//
//    Array_StructurePointer: 참조 블록에 MetaPtr[] 이 들어있고,
//                            각 MetaPtr 가 다시 구조체를 가리킴  (간접 참조)
//    Array_Structure:        참조 블록에 구조체 데이터가 직접 들어있음 (직접 참조)
//
//  [0x00] u64  Pointer  — MetaPtr 인코딩
//  [0x08] u16  Count1
//  [0x0A] u16  Count2
//  [0x0C] u32  Reserved
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
// ── 5. MetaHeader — 파일 헤더, 0x70 bytes
//
//  GTA5 모든 Meta 파일(.ymap .ytyp .ymt …)의 공통 헤더입니다.
//
//  Binary layout:
//   [0x00] u32  VftLo         — 가상 함수 테이블 포인터 하위 32비트
//   [0x04] u32  VftHi
//   [0x08] u64  PagesInfoPtr  — VA: 리소스 페이지 정보 (시스템 세그먼트)
//   [0x10] u32  Magic         — "PRD0" = 0x50524430
//   [0x14] u16  Version       — 0x0079 (121)
//   [0x16] u8   HasEncrypted
//   [0x17] u8   Unknown17
//   [0x18] u32  Unknown18
//   [0x1C] u32  RootBlockIndex   — DataBlocks 배열에서 루트 인덱스 (1-based)
//   [0x20] u64  StructInfosPtr   — VA: StructureInfo 배열
//   [0x28] u64  EnumInfosPtr     — VA: EnumInfo 배열
//   [0x30] u64  DataBlocksPtr    — VA: MetaDataBlock 배열  ← 핵심
//   [0x38] u64  NamePtr          — VA: 파일명 문자열
//   [0x40] u64  EncStrPtr        — VA: 암호화된 문자열
//   [0x48] u16  StructureInfosCount
//   [0x4A] u16  EnumInfosCount
//   [0x4C] u16  DataBlocksCount
//   [0x4E..0x6F] reserved
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
            Out.Log("  [헤더]", "NamePtr",           VA.Describe(NamePtr));
        }
    }
}


// =============================================================================
// ── 6. MetaDataBlock — 16 bytes, DataBlocks 배열의 한 항목
//
//  DataBlocks 배열은 DataBlocksPtr(VA)로 찾습니다.
//  각 항목은 16 bytes:
//
//   [0x00] u32  StructureNameHash  — 데이터 타입 식별자 (MetaName)
//   [0x04] u32  DataLength         — 데이터 바이트 수
//   [0x08] u64  DataPointer        — VA: 실제 데이터 위치
//
//  주요 StructureNameHash 값:
//    0xD3428E33 (3545841574) = CMapData
//    0xCE4571BE (3461354627) = CEntityDef
//    0x6E2B7A63 (1847570019) = POINTER  (MetaPtr 배열)
//    0x100 (256)             = HASH     (u32 해시 배열)
// =============================================================================

class MetaDataBlock
{
    // 주요 MetaName 해시 상수
    public const uint NAME_CMapData   = 3545841574u; // 0xD3428E33
    public const uint NAME_CEntityDef = 3461354627u; // 0xCE4571BE
    public const uint NAME_CCarGen    = 2345238261u; // 0x8BF751B5
    public const uint NAME_POINTER    = 1847570019u; // 0x6E2B7A63 — MetaPtr 배열
    public const uint NAME_HASH       = 256u;        // u32 해시 배열

    public uint  NameHash;
    public uint  DataLength;
    public ulong DataPointer;    // VA → 실제 파일 오프셋
    public int   FileDataOffset; // VA.FileOffset(DataPointer)

    // 16 bytes per block, base = VA.FileOffset(DataBlocksPtr) + index*16
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
        NAME_CMapData   => "CMapData",
        NAME_CEntityDef => "CEntityDef",
        NAME_CCarGen    => "CCarGen",
        NAME_POINTER    => "POINTER[]",
        NAME_HASH       => "HASH[]",
        _               => $"0x{NameHash:X8}",
    };
}


// =============================================================================
// ── 7. Meta — 헤더 + DataBlocks 집합
//
//  DataBlock 내부 포인터 해석 핵심:
//
//    MetaPtr.BlockId(1-based) → DataBlocks[BlockId-1].FileDataOffset
//    + MetaPtr.Offset = 구조체가 있는 파일 오프셋
// =============================================================================

class Meta
{
    public MetaHeader      Header = null!;
    public MetaDataBlock[] Blocks = [];

    // MetaPtr → 파일 오프셋 (-1 = 실패)
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
// ── 8. CMapData — 맵 루트 데이터 (512 bytes)
//
//  Binary layout:
//   [  0] u32  Unused0, Unused1
//   [  8] u32  name         — 맵 이름 해시
//   [ 12] u32  parent       — 부모 맵 이름 해시
//   [ 16] u32  flags
//   [ 20] u32  contentFlags
//   [ 32] f32x3 streamingExtentsMin  [44] pad
//   [ 48] f32x3 streamingExtentsMax  [60] pad
//   [ 64] f32x3 entitiesExtentsMin   [76] pad
//   [ 80] f32x3 entitiesExtentsMax   [92] pad
//   [ 96] MetaArray entities           — Array_StructurePointer (간접)
//   [112] MetaArray containerLods      — Array_Structure (직접)
//   [128] MetaArray boxOccluders       — Array_Structure
//   [144] MetaArray occludeModels      — Array_Structure
//   [160] MetaArray physicsDictionaries — Array_uint
//   [176] rage__fwInstancedMapData     — 48 bytes
//   [224] MetaArray timeCycleModifiers — Array_Structure
//   [240] MetaArray carGenerators      — Array_Structure  ← CCarGen[]
//   [256] CLODLight / CDistantLODLight / CBlockDesc ...
// =============================================================================

class CMapData
{
    public int  FileOffset;

    public uint  Name;
    public uint  Parent;
    public uint  Flags;
    public uint  ContentFlags;

    public (float X, float Y, float Z) StreamingExtentsMin;
    public (float X, float Y, float Z) StreamingExtentsMax;
    public (float X, float Y, float Z) EntitiesExtentsMin;
    public (float X, float Y, float Z) EntitiesExtentsMax;

    public MetaArray Entities;           // 간접 참조 (Array_StructurePointer)
    public MetaArray ContainerLods;      // 직접 참조
    public MetaArray BoxOccluders;       // 직접 참조
    public MetaArray PhysicsDicts;       // u32 해시 배열
    public MetaArray TimeCycleModifiers; // 직접 참조
    public MetaArray CarGenerators;      // 직접 참조 (Array_Structure)

    public static CMapData Read(ResourceReader r, int off)
    {
        return new CMapData
        {
            FileOffset          = off,
            Name                = r.U32(off +  8),
            Parent              = r.U32(off + 12),
            Flags               = r.U32(off + 16),
            ContentFlags        = r.U32(off + 20),
            StreamingExtentsMin = r.F32x3(off + 32),
            StreamingExtentsMax = r.F32x3(off + 48),
            EntitiesExtentsMin  = r.F32x3(off + 64),
            EntitiesExtentsMax  = r.F32x3(off + 80),
            Entities            = ReadArr(r, off +  96),
            ContainerLods       = ReadArr(r, off + 112),
            BoxOccluders        = ReadArr(r, off + 128),
            PhysicsDicts        = ReadArr(r, off + 160),
            TimeCycleModifiers  = ReadArr(r, off + 224),
            CarGenerators       = ReadArr(r, off + 240),
        };
    }

    static MetaArray ReadArr(ResourceReader r, int off) =>
        new MetaArray(r.U64(off), r.U16(off + 8), r.U16(off + 10));

    public void Print(bool verbose)
    {
        Out.Log("  [CMapData]", "file offset",     $"0x{FileOffset:X}  (512 bytes)");
        Out.Log("  [CMapData]", "name",             $"0x{Name:X8}");
        Out.Log("  [CMapData]", "parent",           $"0x{Parent:X8}");
        Out.Log("  [CMapData]", "flags",            $"0x{Flags:X8}");
        Out.Log("  [CMapData]", "entities",         ArrDesc(Entities));
        Out.Log("  [CMapData]", "carGenerators",    ArrDesc(CarGenerators));
        if (verbose)
        {
            Out.Log("  [CMapData]", "contentFlags",     $"0x{ContentFlags:X8}");
            Out.Log("  [CMapData]", "streamExtMin",     Out.Vec3(StreamingExtentsMin));
            Out.Log("  [CMapData]", "streamExtMax",     Out.Vec3(StreamingExtentsMax));
            Out.Log("  [CMapData]", "entityExtMin",     Out.Vec3(EntitiesExtentsMin));
            Out.Log("  [CMapData]", "entityExtMax",     Out.Vec3(EntitiesExtentsMax));
            Out.Log("  [CMapData]", "containerLods",    ArrDesc(ContainerLods));
            Out.Log("  [CMapData]", "boxOccluders",     ArrDesc(BoxOccluders));
            Out.Log("  [CMapData]", "physicsDicts",     ArrDesc(PhysicsDicts));
            Out.Log("  [CMapData]", "timeCycleMods",    ArrDesc(TimeCycleModifiers));
        }
    }

    static string ArrDesc(MetaArray a) =>
        a.IsEmpty ? "(empty)" : $"count={a.Count1}  ptr={a.Ptr}";
}


// =============================================================================
// ── 9. CEntityDef — 엔티티(오브젝트) 배치 정보 (128 bytes)
//
//  CMapData.entities 가 Array_StructurePointer 이므로 두 단계로 참조됩니다:
//    1. entities.Ptr → POINTER[] 블록 (MetaPtr 배열)
//    2. 각 MetaPtr → CEntityDef 블록 내 특정 오프셋
//
//  Binary layout:
//   [  0] u32  Unused x2
//   [  8] u32  archetypeName   — 모델 이름 해시 (JenkHash)
//   [ 12] u32  flags
//   [ 16] u32  guid
//   [ 32] f32x3 position
//   [ 48] f32x4 rotation      — 쿼터니언 XYZW
//   [ 64] f32  scaleXY
//   [ 68] f32  scaleZ
//   [ 72] s32  parentIndex    — -1 = 루트
//   [ 76] f32  lodDist
//   [ 80] f32  childLodDist
//   [ 84] s32  lodLevel       — 0=HD 1=LOD 2=SLOD1 3=SLOD2 4=SLOD3 6=OrphanHD
//   [ 88] u32  numChildren
//   [ 92] s32  priorityLevel
// =============================================================================

class CEntityDef
{
    public int   FileOffset;
    public uint  ArchetypeName;
    public uint  Flags;
    public uint  Guid;
    public (float X, float Y, float Z)          Position;
    public (float X, float Y, float Z, float W) Rotation;
    public float ScaleXY;
    public float ScaleZ;
    public int   ParentIndex;
    public float LodDist;
    public float ChildLodDist;
    public int   LodLevel;
    public uint  NumChildren;
    public int   PriorityLevel;

    public string LodLevelName => LodLevel switch
    {
        0 => "HD",
        1 => "LOD",
        2 => "SLOD1",
        3 => "SLOD2",
        4 => "SLOD3",
        5 => "SLOD4",
        6 => "OrphanHD",
        _ => $"LOD({LodLevel})",
    };

    public static CEntityDef Read(ResourceReader r, int off)
    {
        return new CEntityDef
        {
            FileOffset    = off,
            ArchetypeName = r.U32(off +  8),
            Flags         = r.U32(off + 12),
            Guid          = r.U32(off + 16),
            Position      = r.F32x3(off + 32),
            Rotation      = r.F32x4(off + 48),
            ScaleXY       = r.F32(off + 64),
            ScaleZ        = r.F32(off + 68),
            ParentIndex   = (int)r.U32(off + 72),
            LodDist       = r.F32(off + 76),
            ChildLodDist  = r.F32(off + 80),
            LodLevel      = (int)r.U32(off + 84),
            NumChildren   = r.U32(off + 88),
            PriorityLevel = (int)r.U32(off + 92),
        };
    }

    public string Summary() =>
        $"arch=0x{ArchetypeName:X8}  " +
        $"pos=({Position.X:F1},{Position.Y:F1},{Position.Z:F1})  " +
        $"lod={LodLevelName,-8}  children={NumChildren}";

    public void Print()
    {
        Out.Log("    ", "archetypeName", $"0x{ArchetypeName:X8}");
        Out.Log("    ", "flags",         $"0x{Flags:X8}");
        Out.Log("    ", "position",      Out.Vec3(Position));
        Out.Log("    ", "rotation",      Out.Vec4(Rotation));
        Out.Log("    ", "scale",         $"XY={ScaleXY:F3}  Z={ScaleZ:F3}");
        Out.Log("    ", "parentIndex",   $"{ParentIndex}");
        Out.Log("    ", "lodDist",       $"{LodDist:F1}  childLod={ChildLodDist:F1}");
        Out.Log("    ", "lodLevel",      $"{LodLevel}  ({LodLevelName})");
        Out.Log("    ", "numChildren",   $"{NumChildren}");
    }
}


// =============================================================================
// ── 10. CCarGen — 차량 생성 정보 (80 bytes)
//
//  CMapData.carGenerators 가 Array_Structure 이므로 한 단계만 참조됩니다:
//    1. carGenerators.Ptr → CCarGen[] 블록 (구조체 인라인)
//
//  Binary layout:
//   [  0] u32 x4  Unused (16 bytes)
//   [ 16] f32x3   position
//   [ 28] f32     (pad)
//   [ 32] f32     orientX
//   [ 36] f32     orientY
//   [ 40] f32     perpendicularLength
//   [ 44] u32     carModel    — 차종 이름 해시
//   [ 48] u32     flags
//   [ 52] s32 x4  bodyColorRemap1-4
//   [ 68] u32     popGroup    — 생성 그룹 해시
//   [ 72] s8      livery
// =============================================================================

class CCarGen
{
    public int   FileOffset;
    public (float X, float Y, float Z) Position;
    public float OrientX;
    public float OrientY;
    public uint  CarModel;
    public uint  Flags;
    public uint  PopGroup;
    public sbyte Livery;

    public static CCarGen Read(ResourceReader r, int off)
    {
        return new CCarGen
        {
            FileOffset = off,
            Position   = r.F32x3(off + 16),
            OrientX    = r.F32(off + 32),
            OrientY    = r.F32(off + 36),
            CarModel   = r.U32(off + 44),
            Flags      = r.U32(off + 48),
            PopGroup   = r.U32(off + 68),
            Livery     = (sbyte)r.U8(off + 72),
        };
    }

    public string Summary() =>
        $"model=0x{CarModel:X8}  " +
        $"pos=({Position.X:F1},{Position.Y:F1},{Position.Z:F1})  " +
        $"pop=0x{PopGroup:X8}  livery={Livery}";
}


// =============================================================================
// ── 11. YmapFile — 파일 로더
//
//  파싱 흐름:
//    ① MetaHeader 읽기 (0x70 bytes)
//       → Magic 검증 ("PRD0")
//       → DataBlocksPtr(VA), DataBlocksCount, RootBlockIndex 확보
//
//    ② DataBlocks 배열 읽기
//       → 각 16 bytes: (NameHash, DataLength, DataPointer)
//       → DataPointer(VA) → FileDataOffset = VA & 0x0FFFFFFF
//
//    ③ RootBlock = DataBlocks[RootBlockIndex - 1]
//       → CMapData 읽기 (RootBlock.FileDataOffset 에서)
//
//    ④ CMapData.entities 읽기 (간접 참조 2단계)
//       entities.Ptr → POINTER[] 블록 → 각 MetaPtr → CEntityDef 블록
//
//    ⑤ CMapData.carGenerators 읽기 (직접 참조 1단계)
//       carGenerators.Ptr → CCarGen[] 블록 (구조체 인라인)
// =============================================================================

class YmapFile
{
    public string         FilePath = "";
    public int            FileSize;
    public Meta           MetaInfo = null!;
    public CMapData       MapData  = null!;
    public CEntityDef[]   Entities = [];
    public CCarGen[]      CarGens  = [];

    public static YmapFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r   = new ResourceReader(data);
        var ymp = new YmapFile { FilePath = path, FileSize = data.Length };

        // ── ① 헤더 읽기 ──────────────────────────────────────────────────
        var hdr = MetaHeader.Read(r);
        if (hdr.IsRsc7)
            throw new InvalidDataException("RSC7 압축 파일 — 압축 해제 후 로딩하세요.");
        if (!hdr.IsMeta)
            throw new InvalidDataException($"Meta 매직이 아닙니다: 0x{hdr.Magic:X8}");

        // ── ② DataBlocks 배열 읽기 ───────────────────────────────────────
        var meta = Meta.Read(r);
        ymp.MetaInfo = meta;

        // ── ③ 루트 블록 = CMapData ──────────────────────────────────────
        int rootIdx = (int)hdr.RootBlockIndex - 1;
        if (rootIdx < 0 || rootIdx >= meta.Blocks.Length)
            throw new InvalidDataException(
                $"RootBlockIndex({hdr.RootBlockIndex}) 가 범위를 벗어남 (blocks={meta.Blocks.Length}).");

        var rootBlock = meta.Blocks[rootIdx];
        ymp.MapData   = CMapData.Read(r, rootBlock.FileDataOffset);

        // ── ④ Entities 읽기 (간접: StructurePointer 배열) ────────────────
        ymp.Entities  = ReadEntities(r, meta, ymp.MapData.Entities);

        // ── ⑤ CarGenerators 읽기 (직접: Structure 배열) ──────────────────
        ymp.CarGens   = ReadCarGens(r, meta, ymp.MapData.CarGenerators);

        return ymp;
    }

    // 간접 참조: POINTER[] 블록 → 각 MetaPtr → CEntityDef
    static CEntityDef[] ReadEntities(ResourceReader r, Meta meta, MetaArray arr)
    {
        if (arr.IsEmpty) return [];

        int ptrArrayOff = meta.ResolveMetaPtr(arr.Ptr);
        if (ptrArrayOff < 0) return [];

        var list = new List<CEntityDef>(arr.Count1);
        for (int i = 0; i < arr.Count1; i++)
        {
            ulong raw    = r.U64(ptrArrayOff + i * 8);
            var   entPtr = new MetaPtr(raw);
            int   off    = meta.ResolveMetaPtr(entPtr);
            if (off >= 0)
                list.Add(CEntityDef.Read(r, off));
        }
        return list.ToArray();
    }

    // 직접 참조: CCarGen[] 가 블록에 인라인으로 나열됨
    static CCarGen[] ReadCarGens(ResourceReader r, Meta meta, MetaArray arr)
    {
        if (arr.IsEmpty) return [];

        int baseOff = meta.ResolveMetaPtr(arr.Ptr);
        if (baseOff < 0) return [];

        const int SIZE = 80;
        var list = new List<CCarGen>(arr.Count1);
        for (int i = 0; i < arr.Count1; i++)
            list.Add(CCarGen.Read(r, baseOff + i * SIZE));
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

    public static string Vec4((float X, float Y, float Z, float W) v) =>
        $"({v.X:F3}, {v.Y:F3}, {v.Z:F3}, {v.W:F3})";
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
            : Directory.GetFiles(target, "*.ymap", SearchOption.AllDirectories);

        Console.WriteLine($".ymap 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"══ {Path.GetFileName(path)}");
            try
            {
                var ymp = YmapFile.Load(path);
                ok++;

                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  파일 크기: {ymp.FileSize:N0} bytes");
                    Console.WriteLine();

                    ymp.MetaInfo.Header.Print(verbose);
                    Console.WriteLine();

                    Console.WriteLine("  [DataBlocks]");
                    for (int i = 0; i < ymp.MetaInfo.Blocks.Length; i++)
                    {
                        var b = ymp.MetaInfo.Blocks[i];
                        Console.WriteLine(
                            $"    [{i+1:D2}] {b.TypeLabel,-16}  " +
                            $"len={b.DataLength,6}  " +
                            $"data={VA.Describe(b.DataPointer)}");
                    }
                    Console.WriteLine();

                    ymp.MapData.Print(verbose);
                    Console.WriteLine();

                    if (ymp.Entities.Length > 0)
                    {
                        Console.WriteLine($"  [Entities]  ({ymp.Entities.Length}개)");
                        int show = verbose
                            ? ymp.Entities.Length
                            : Math.Min(ymp.Entities.Length, 10);
                        for (int i = 0; i < show; i++)
                        {
                            Console.WriteLine($"    [{i:D3}] {ymp.Entities[i].Summary()}");
                            if (verbose) ymp.Entities[i].Print();
                        }
                        if (show < ymp.Entities.Length)
                            Console.WriteLine(
                                $"    ... 이하 {ymp.Entities.Length - show}개 생략" +
                                $" (--verbose 로 전체 출력)");
                        Console.WriteLine();
                    }

                    if (ymp.CarGens.Length > 0)
                    {
                        Console.WriteLine($"  [CarGenerators]  ({ymp.CarGens.Length}개)");
                        foreach (var cg in ymp.CarGens)
                            Console.WriteLine($"    {cg.Summary()}");
                        Console.WriteLine();
                    }
                }
                else
                {
                    // 폴더 요약 모드
                    Console.WriteLine(
                        $"  name=0x{ymp.MapData.Name:X8}  " +
                        $"entities={ymp.Entities.Length}  " +
                        $"carGens={ymp.CarGens.Length}  " +
                        $"blocks={ymp.MetaInfo.Blocks.Length}");
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

        Console.WriteLine($"── 결과: 성공 {ok} / 실패 {fail} / 전체 {files.Length} ──");
        Console.Write( "계속하려면 아무키나 누르세요 : " ) ;
        Console.Read() ;
    }
}

} // namespace YmapReader
