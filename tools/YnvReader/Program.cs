// =============================================================================
// YnvReader — GTA5 .ynv 파일 파서 (학습용, 독립 실행)
//
// .ynv 는 GTA5 의 내비게이션 메시(NavMesh) 파일입니다.
// 보행자 및 차량의 AI 경로 탐색에 사용됩니다.
//
// ┌─ 파일 전체 구조 ────────────────────────────────────────────────────────┐
// │  .ynv 는 리소스(RSC) 포맷 — .ybn 과 같은 VA 시스템을 사용합니다.        │
// │  파일 오프셋 0 = 가상주소 0x50000000 (System 세그먼트 시작)              │
// │  포인터를 파일 오프셋으로: fileOff = ptr & 0x0FFFFFFF                    │
// │                                                                          │
// │  [0x00] NavMesh 루트 블록 (368 bytes)                                   │
// │    → VerticesPointer   → NavMeshList<NavMeshVertex>  (각 6 bytes)       │
// │    → IndicesPointer    → NavMeshList<u16>             (폴리 인덱스)     │
// │    → EdgesPointer      → NavMeshList<NavMeshEdge>    (각 8 bytes)       │
// │    → PolysPointer      → NavMeshList<NavMeshPoly>    (각 48 bytes)      │
// │    → SectorTreePointer → NavMeshSector               (쿼드트리 루트)   │
// │    → PortalsPointer    → NavMeshPortal[]             (각 28 bytes)      │
// │    → PortalLinksPointer → u16[]                       (포탈 링크)       │
// └─────────────────────────────────────────────────────────────────────────┘
//
// ┌─ NavMeshList<T> 구조 ───────────────────────────────────────────────────┐
// │  NavMeshList 헤더 (48 bytes):                                            │
// │   [0x08] u32 ItemCount, [0x10] ListPartsPtr, [0x20] ListPartsCount      │
// │  ListPart (16 bytes):                                                    │
// │   [0x00] u64 Pointer → T[] 데이터, [0x08] u32 Count                    │
// │  ※ 파트 하나당 최대 16KB, 큰 배열은 여러 파트로 분산                    │
// └─────────────────────────────────────────────────────────────────────────┘
//
// ┌─ 좌표 복원 공식 ────────────────────────────────────────────────────────┐
// │  NavMeshVertex = 정규화된 ushort (0..65535)                              │
// │  WorldPos = SectorTree.AABBMin + (vertex/65535) * NavMesh.AABBSize      │
// └─────────────────────────────────────────────────────────────────────────┘
//
// 실행:  dotnet run                                     (기본 경로)
//        dotnet run -- "경로/dir"                       (디렉토리)
//        dotnet run -- "경로/file.ynv" --verbose        (상세)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace YnvReader
{

// =============================================================================
// ── 1. VA — GTA5 가상 주소 → 파일 오프셋
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
        IsValid(ptr) ? $"0x{ptr:X8} → file@0x{FileOffset(ptr):X}" : "(null)";
}


// =============================================================================
// ── 2. ResourceReader — byte[] 래퍼
// =============================================================================

class ResourceReader
{
    readonly byte[] _d;
    public ResourceReader(byte[] data) => _d = data;
    public int    Length        => _d.Length;
    public byte   U8 (int off)  => _d[off];
    public short  I16(int off)  => BitConverter.ToInt16(_d, off);
    public ushort U16(int off)  => BitConverter.ToUInt16(_d, off);
    public uint   U32(int off)  => BitConverter.ToUInt32(_d, off);
    public ulong  U64(int off)  => BitConverter.ToUInt64(_d, off);
    public float  F32(int off)  => BitConverter.ToSingle(_d, off);
    public (float X, float Y, float Z) F32x3(int off) => (F32(off), F32(off+4), F32(off+8));
    public int PtrOff(int off) => VA.FileOffset(U64(off));
}


// =============================================================================
// ── 3. 열거형
// =============================================================================

[Flags]
enum NavMeshFlags : uint
{
    None     = 0,
    Polygons = 1,
    Portals  = 2,
    Vehicle  = 4,    // 차량용 navmesh (단일 레벨 쿼드트리)
    Unknown8 = 8,
    Unknown16= 16,
}


// =============================================================================
// ── 4. NavMeshVertex — 정규화된 ushort 좌표 (6 bytes)
//
//  [0] u16 X, [2] u16 Y, [4] u16 Z  →  0..65535 = 0.0..1.0 (정규화)
//  월드 좌표 = AABBMin + (X/65535, Y/65535, Z/65535) × AABBSize
// =============================================================================

struct NavMeshVertex
{
    public ushort X, Y, Z;
    public const int SIZE = 6;

    public static NavMeshVertex Read(ResourceReader r, int off) =>
        new() { X = r.U16(off), Y = r.U16(off+2), Z = r.U16(off+4) };

    public (float X, float Y, float Z) ToWorld(
        (float X, float Y, float Z) aabbMin,
        (float X, float Y, float Z) aabbSize) => (
        aabbMin.X + (X / 65535f) * aabbSize.X,
        aabbMin.Y + (Y / 65535f) * aabbSize.Y,
        aabbMin.Z + (Z / 65535f) * aabbSize.Z);
}


// =============================================================================
// ── 5. NavMeshEdgePart — 엣지의 한쪽 폴리곤 참조 (4 bytes, bit-packed)
//
//  bits[0..4]  = AreaIDInd  — AdjAreaIDs 배열 인덱스 (0..31)
//  bits[5..18] = PolyID     — 인접 폴리곤 인덱스 (0x3FFF = 없음)
//  bits[19..20]= Unk2
//  bits[21..31]= Unk3
// =============================================================================

struct NavMeshEdgePart
{
    public uint Value;
    public uint AreaIDInd => (Value >> 0) & 0x1F;
    public uint PolyID    => (Value >> 5) & 0x3FFF;
    public bool IsEmpty   => PolyID == 0x3FFF;
}


// =============================================================================
// ── 6. NavMeshEdge — 두 폴리곤의 공유 엣지 (8 bytes)
//
//  [0] NavMeshEdgePart Poly1  (4 bytes)
//  [4] NavMeshEdgePart Poly2  (4 bytes)
// =============================================================================

struct NavMeshEdge
{
    public NavMeshEdgePart Poly1, Poly2;
    public const int SIZE = 8;

    public static NavMeshEdge Read(ResourceReader r, int off) => new()
    {
        Poly1 = new() { Value = r.U32(off) },
        Poly2 = new() { Value = r.U32(off+4) },
    };
}


// =============================================================================
// ── 7. NavMeshPoly — 내비게이션 폴리곤 (48 bytes)
//
//  [ 0] u16 PolyFlags0   — 타입 플래그 (하위 바이트 = Flags1)
//  [ 2] u16 IndexFlags   — bits[5..15] = 꼭짓점 개수
//  [ 4] u16 IndexID      — Indices 배열의 시작 인덱스
//  [ 6] u16 AreaID       — 이 .ynv 파일의 AreaID 와 동일
//  [ 8] u32×4 unused
//  [24] i16×6 CellAABB   — 폴리의 AABB (×4 고정소수)
//  [36] u32 PolyFlags1   — 세부 타입 플래그 (IsRoad, IsInterior 등)
//  [40] u32 PolyFlags2   — 경사 방향, UnkX/UnkY
//  [44] u32 PartFlags    — PartID, PortalLinkCount, PortalLinkID
// =============================================================================

struct NavMeshPoly
{
    public ushort PolyFlags0, IndexFlags, IndexID, AreaID;
    public short  CellMinX, CellMaxX, CellMinY, CellMaxY, CellMinZ, CellMaxZ;
    public uint   PolyFlags1, PolyFlags2, PartFlags;

    public const int SIZE = 48;

    public int   IndexCount      => (IndexFlags >> 5);
    public ushort PartID         => (ushort)((PartFlags >> 4) & 0xFF);
    public byte  PortalLinkCount => (byte)((PartFlags >> 12) & 0x7);
    public uint  PortalLinkID    => (PartFlags >> 15) & 0x1FFFF;

    public bool IsFootpath    => (PolyFlags0 & 0x04) != 0;
    public bool IsUnderground => (PolyFlags0 & 0x08) != 0;
    public bool IsSteepSlope  => (PolyFlags0 & 0x40) != 0;
    public bool IsWater       => (PolyFlags0 & 0x80) != 0;
    public bool IsInterior    => (PolyFlags1 & 0x40) != 0;
    public bool IsFlatGround  => (PolyFlags1 & 0x200) != 0;
    public bool IsRoad        => (PolyFlags1 & 0x400) != 0;
    public bool IsTrainTrack  => (PolyFlags1 & 0x1000) != 0;
    public bool IsShallowWater=> (PolyFlags1 & 0x2000) != 0;

    public static NavMeshPoly Read(ResourceReader r, int off) => new()
    {
        PolyFlags0 = r.U16(off),
        IndexFlags = r.U16(off + 2),
        IndexID    = r.U16(off + 4),
        AreaID     = r.U16(off + 6),
        CellMinX   = r.I16(off + 24),
        CellMaxX   = r.I16(off + 26),
        CellMinY   = r.I16(off + 28),
        CellMaxY   = r.I16(off + 30),
        CellMinZ   = r.I16(off + 32),
        CellMaxZ   = r.I16(off + 34),
        PolyFlags1 = r.U32(off + 36),
        PolyFlags2 = r.U32(off + 40),
        PartFlags  = r.U32(off + 44),
    };

    public string FlagSummary()
    {
        var parts = new List<string>();
        if (IsRoad)         parts.Add("Road");
        if (IsFootpath)     parts.Add("Footpath");
        if (IsInterior)     parts.Add("Interior");
        if (IsWater)        parts.Add("Water");
        if (IsShallowWater) parts.Add("ShallowWater");
        if (IsTrainTrack)   parts.Add("TrainTrack");
        if (IsFlatGround)   parts.Add("FlatGround");
        if (IsSteepSlope)   parts.Add("SteepSlope");
        if (IsUnderground)  parts.Add("Underground");
        return parts.Count > 0 ? string.Join("|", parts) : "Normal";
    }
}


// =============================================================================
// ── 8. NavMeshPortal — 두 폴리곤 영역 사이의 통과 지점 (28 bytes)
//
//  포탈은 AI 가 이동 가능한 '경계선'을 나타냅니다.
//  차량: 인접 cell 경계 | 보행자: 인테리어 출입구 등
//
//  [ 0] u8  Type       — 포탈 종류 (1=Cell 경계, 2=Portal, 3=?)
//  [ 1] u8  Angle      — 방향 (0..255 → 0..2π)
//  [ 2] u16 FlagsUnk   — 항상 0
//  [ 4] u16×3 PositionFrom — 정규화 출발 위치
//  [10] u16×3 PositionTo   — 정규화 도착 위치
//  [16] u16 PolyIDFrom1
//  [18] u16 PolyIDFrom2  (항상 From1 과 동일)
//  [20] u16 PolyIDTo1
//  [22] u16 PolyIDTo2    (항상 To1 과 동일)
//  [24] u32 AreaFlags    — bits[0..13]=AreaIDFrom, bits[14..27]=AreaIDTo
// =============================================================================

struct NavMeshPortal
{
    public byte          Type, Angle;
    public ushort        FlagsUnk;
    public NavMeshVertex PositionFrom, PositionTo;
    public ushort        PolyIDFrom1, PolyIDFrom2;
    public ushort        PolyIDTo1,   PolyIDTo2;
    public uint          AreaFlags;

    public const int SIZE = 28;

    public ushort AreaIDFrom => (ushort)(AreaFlags & 0x3FFF);
    public ushort AreaIDTo   => (ushort)((AreaFlags >> 14) & 0x3FFF);

    public static NavMeshPortal Read(ResourceReader r, int off) => new()
    {
        Type         = r.U8(off),
        Angle        = r.U8(off + 1),
        FlagsUnk     = r.U16(off + 2),
        PositionFrom = NavMeshVertex.Read(r, off + 4),
        PositionTo   = NavMeshVertex.Read(r, off + 10),
        PolyIDFrom1  = r.U16(off + 16),
        PolyIDFrom2  = r.U16(off + 18),
        PolyIDTo1    = r.U16(off + 20),
        PolyIDTo2    = r.U16(off + 22),
        AreaFlags    = r.U32(off + 24),
    };
}


// =============================================================================
// ── 9. NavMeshPoint — AI 경로 노드 (8 bytes)
//
//  SectorTree 의 잎 노드에 저장되는 보행자 집합/분산 포인트
//
//  [0] u16×3 Position — 정규화 위치
//  [6] u8 Angle       — 방향
//  [7] u8 Type        — 포인트 종류 (0..5, 128, 171, 254)
// =============================================================================

struct NavMeshPoint
{
    public ushort X, Y, Z;
    public byte   Angle, Type;

    public const int SIZE = 8;

    public static NavMeshPoint Read(ResourceReader r, int off) => new()
    {
        X = r.U16(off), Y = r.U16(off+2), Z = r.U16(off+4),
        Angle = r.U8(off+6), Type = r.U8(off+7),
    };
}


// =============================================================================
// ── 10. SectorNode — NavMeshSector 쿼드트리 노드 (파싱 결과)
//
//  NavMeshSector 바이너리 (96 bytes):
//   [ 0] f32×4 AABBMin  (16 bytes, W=NaN)
//   [16] f32×4 AABBMax  (16 bytes, W=NaN)
//   [32] i16×6 CellAABB (12 bytes)
//   [44] u64 DataPointer
//   [52] u64 SubTree1..4Pointer (각 8 bytes)
//   [84..95] u32×3 unused
//
//  NavMeshSectorData (32 bytes):
//   [ 0] u32 PointsStartID
//   [ 4] u32 unused
//   [ 8] u64 PolyIDsPointer
//   [16] u64 PointsPointer
//   [24] u16 PolyIDsCount
//   [26] u16 PointsCount
//   [28] u32 unused
// =============================================================================

class SectorNode
{
    public (float X, float Y, float Z) AABBMin;
    public (float X, float Y, float Z) AABBMax;
    public NavMeshPoint[] Points   = [];
    public ushort[]       PolyIDs  = [];
    public List<SectorNode> Children = new();
    public bool IsLeaf => Children.Count == 0;
}


// =============================================================================
// ── 11. NavMeshParser — NavMeshList 및 SectorTree 파서
// =============================================================================

static class NavMeshParser
{
    class ListInfo
    {
        public uint ItemCount;
        public uint PartsCount;
        public List<(int DataOff, uint Count)> Parts = new();
    }

    static ListInfo ReadListInfo(ResourceReader r, int listOff)
    {
        var info = new ListInfo
        {
            ItemCount  = r.U32(listOff + 8),
            PartsCount = r.U32(listOff + 32),
        };
        if (info.PartsCount == 0) return info;
        int partsArrOff = r.PtrOff(listOff + 16);
        for (int i = 0; i < info.PartsCount; i++)
        {
            int   partOff = partsArrOff + i * 16;
            ulong dataPtr = r.U64(partOff);
            uint  cnt     = r.U32(partOff + 8);
            if (VA.IsValid(dataPtr))
                info.Parts.Add((VA.FileOffset(dataPtr), cnt));
        }
        return info;
    }

    public static NavMeshVertex[] ReadVertices(ResourceReader r, int listOff)
    {
        var info = ReadListInfo(r, listOff);
        var list = new List<NavMeshVertex>((int)info.ItemCount);
        foreach (var (dataOff, cnt) in info.Parts)
            for (uint i = 0; i < cnt; i++)
                list.Add(NavMeshVertex.Read(r, dataOff + (int)(i * NavMeshVertex.SIZE)));
        return list.ToArray();
    }

    public static ushort[] ReadIndices(ResourceReader r, int listOff)
    {
        var info = ReadListInfo(r, listOff);
        var list = new List<ushort>((int)info.ItemCount);
        foreach (var (dataOff, cnt) in info.Parts)
            for (uint i = 0; i < cnt; i++)
                list.Add(r.U16(dataOff + (int)(i * 2)));
        return list.ToArray();
    }

    public static NavMeshEdge[] ReadEdges(ResourceReader r, int listOff)
    {
        var info = ReadListInfo(r, listOff);
        var list = new List<NavMeshEdge>((int)info.ItemCount);
        foreach (var (dataOff, cnt) in info.Parts)
            for (uint i = 0; i < cnt; i++)
                list.Add(NavMeshEdge.Read(r, dataOff + (int)(i * NavMeshEdge.SIZE)));
        return list.ToArray();
    }

    public static NavMeshPoly[] ReadPolys(ResourceReader r, int listOff)
    {
        var info = ReadListInfo(r, listOff);
        var list = new List<NavMeshPoly>((int)info.ItemCount);
        foreach (var (dataOff, cnt) in info.Parts)
            for (uint i = 0; i < cnt; i++)
                list.Add(NavMeshPoly.Read(r, dataOff + (int)(i * NavMeshPoly.SIZE)));
        return list.ToArray();
    }

    public static SectorNode ReadSector(ResourceReader r, int off, int depth = 0)
    {
        var node = new SectorNode
        {
            AABBMin = r.F32x3(off),
            AABBMax = r.F32x3(off + 16),
        };

        ulong dataPtr = r.U64(off + 44);
        if (VA.IsValid(dataPtr))
        {
            int dOff = VA.FileOffset(dataPtr);
            ulong polyIDsPtr    = r.U64(dOff + 8);
            ushort polyIDsCnt   = r.U16(dOff + 24);
            ulong pointsPtr     = r.U64(dOff + 16);
            ushort pointsCnt    = r.U16(dOff + 26);

            if (polyIDsCnt > 0 && VA.IsValid(polyIDsPtr))
            {
                int poff = VA.FileOffset(polyIDsPtr);
                node.PolyIDs = new ushort[polyIDsCnt];
                for (int i = 0; i < polyIDsCnt; i++)
                    node.PolyIDs[i] = r.U16(poff + i * 2);
            }
            if (pointsCnt > 0 && VA.IsValid(pointsPtr))
            {
                int poff = VA.FileOffset(pointsPtr);
                node.Points = new NavMeshPoint[pointsCnt];
                for (int i = 0; i < pointsCnt; i++)
                    node.Points[i] = NavMeshPoint.Read(r, poff + i * NavMeshPoint.SIZE);
            }
        }

        if (depth < 5)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                ulong subPtr = r.U64(off + 52 + sub * 8);
                if (VA.IsValid(subPtr))
                    node.Children.Add(ReadSector(r, VA.FileOffset(subPtr), depth + 1));
            }
        }
        return node;
    }

    public static void CollectSectorStats(SectorNode root,
        out int totalLeaves, out int totalPoints, out int totalPolyRefs)
    {
        totalLeaves = 0; totalPoints = 0; totalPolyRefs = 0;
        var stack = new Stack<SectorNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsLeaf)
            {
                totalLeaves++;
                totalPoints  += n.Points.Length;
                totalPolyRefs += n.PolyIDs.Length;
            }
            foreach (var c in n.Children) stack.Push(c);
        }
    }
}


// =============================================================================
// ── 12. YnvFile — .ynv 파일 전체 파서
//
//  NavMesh 루트 블록 오프셋 맵 (총 368 bytes):
//   [0x00] u32  FileVft
//   [0x04] u32  FileUnknown
//   [0x08] u64  FilePagesInfoPtr
//   [0x10] u32  ContentFlags       ← NavMeshFlags
//   [0x14] u32  VersionUnk1        (0x00010011)
//   [0x20] f32×16 Transform        (항등 행렬)
//   [0x60] f32×3  AABBSize
//   [0x6C] u32  AABBUnk
//   [0x70] u64  VerticesPointer
//   [0x80] u64  IndicesPointer
//   [0x88] u64  EdgesPointer
//   [0x90] u32  EdgesIndicesCount
//   [0x94] u32  AdjAreaIDsCount    + [0x98] u32×32 AdjAreaIDs (총 132 bytes)
//   [0x118] u64 PolysPointer
//   [0x120] u64 SectorTreePointer
//   [0x128] u64 PortalsPointer
//   [0x130] u64 PortalLinksPointer
//   [0x138] u32 VerticesCount
//   [0x13C] u32 PolysCount
//   [0x140] u32 AreaID             ← CellX + CellY × 100
//   [0x144] u32 TotalBytes
//   [0x148] u32 PointsCount
//   [0x14C] u32 PortalsCount
//   [0x150] u32 PortalLinksCount
//   [0x160] u32 VersionUnk2        (0x85CB3561=보행자, 0=차량)
// =============================================================================

class YnvFile
{
    public string     FilePath = "";
    public int        FileSize;

    // NavMesh 헤더 필드
    public NavMeshFlags ContentFlags;
    public uint  VersionUnk1, VersionUnk2;
    public (float X, float Y, float Z) AABBSize;
    public uint  VerticesCount, PolysCount, AreaID;
    public uint  TotalBytes, PointsCount;
    public uint  PortalsCount, PortalLinksCount, EdgesIndicesCount;
    public uint[] AdjAreaIDs = [];

    // 파싱된 데이터
    public NavMeshVertex[] Vertices    = [];
    public ushort[]        Indices     = [];
    public NavMeshEdge[]   Edges       = [];
    public NavMeshPoly[]   Polys       = [];
    public NavMeshPortal[] Portals     = [];
    public ushort[]        PortalLinks = [];
    public SectorNode?     SectorTree;
    public (float X, float Y, float Z) AABBMin;

    public int  CellX     => (int)(AreaID % 100);
    public int  CellY     => (int)(AreaID / 100);
    public bool IsVehicle => (ContentFlags & NavMeshFlags.Vehicle) != 0;

    public static YnvFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r = new ResourceReader(data);
        var f = new YnvFile { FilePath = path, FileSize = data.Length };

        const int ROOT = 0;

        f.ContentFlags      = (NavMeshFlags)r.U32(ROOT + 0x10);
        f.VersionUnk1       = r.U32(ROOT + 0x14);
        f.AABBSize          = r.F32x3(ROOT + 0x60);

        ulong vertPtr       = r.U64(ROOT + 0x70);
        ulong indPtr        = r.U64(ROOT + 0x80);
        ulong edgePtr       = r.U64(ROOT + 0x88);
        f.EdgesIndicesCount = r.U32(ROOT + 0x90);

        uint adjCount = r.U32(ROOT + 0x94);
        f.AdjAreaIDs = new uint[Math.Min(adjCount, 32)];
        for (int i = 0; i < f.AdjAreaIDs.Length; i++)
            f.AdjAreaIDs[i] = r.U32(ROOT + 0x98 + i * 4);

        ulong polyPtr       = r.U64(ROOT + 0x118);
        ulong sectorPtr     = r.U64(ROOT + 0x120);
        ulong portalPtr     = r.U64(ROOT + 0x128);
        ulong portalLinkPtr = r.U64(ROOT + 0x130);

        f.VerticesCount    = r.U32(ROOT + 0x138);
        f.PolysCount       = r.U32(ROOT + 0x13C);
        f.AreaID           = r.U32(ROOT + 0x140);
        f.TotalBytes       = r.U32(ROOT + 0x144);
        f.PointsCount      = r.U32(ROOT + 0x148);
        f.PortalsCount     = r.U32(ROOT + 0x14C);
        f.PortalLinksCount = r.U32(ROOT + 0x150);
        f.VersionUnk2      = r.U32(ROOT + 0x160);

        // SectorTree 먼저 (AABBMin 필요)
        if (VA.IsValid(sectorPtr))
        {
            int sOff = VA.FileOffset(sectorPtr);
            f.AABBMin    = r.F32x3(sOff);
            f.SectorTree = NavMeshParser.ReadSector(r, sOff);
        }

        if (VA.IsValid(vertPtr))  f.Vertices    = NavMeshParser.ReadVertices(r, VA.FileOffset(vertPtr));
        if (VA.IsValid(indPtr))   f.Indices     = NavMeshParser.ReadIndices(r, VA.FileOffset(indPtr));
        if (VA.IsValid(edgePtr))  f.Edges       = NavMeshParser.ReadEdges(r, VA.FileOffset(edgePtr));
        if (VA.IsValid(polyPtr))  f.Polys       = NavMeshParser.ReadPolys(r, VA.FileOffset(polyPtr));

        if (VA.IsValid(portalPtr) && f.PortalsCount > 0)
        {
            int pOff = VA.FileOffset(portalPtr);
            f.Portals = new NavMeshPortal[f.PortalsCount];
            for (int i = 0; i < (int)f.PortalsCount; i++)
                f.Portals[i] = NavMeshPortal.Read(r, pOff + i * NavMeshPortal.SIZE);
        }
        if (VA.IsValid(portalLinkPtr) && f.PortalLinksCount > 0)
        {
            int pOff = VA.FileOffset(portalLinkPtr);
            f.PortalLinks = new ushort[f.PortalLinksCount];
            for (int i = 0; i < (int)f.PortalLinksCount; i++)
                f.PortalLinks[i] = r.U16(pOff + i * 2);
        }

        return f;
    }
}


// =============================================================================
// ── 13. 출력 헬퍼
// =============================================================================

static class Out
{
    public static void Log(string prefix, string field, string value) =>
        Console.WriteLine($"{prefix}  {field,-30} {value}");

    public static string Vec3((float X, float Y, float Z) v) =>
        $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
}


// =============================================================================
// ── 14. 진입점
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

        string[] files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.ynv", SearchOption.AllDirectories)
            : File.Exists(target)
            ? [target]
            : [];

        if (files.Length == 0)
        {
            Console.WriteLine($"파일을 찾을 수 없습니다: {target}");
            Console.WriteLine();
            Console.WriteLine(".ynv 파일은 GTA5 RPF 아카이브 안에 있습니다:");
            Console.WriteLine("  common.rpf → common/data/navmesh/*.ynv  (격자 보행자 navmesh)");
            Console.WriteLine("  예: 0000.ynv (CellX=0, CellY=0), 0101.ynv (CellX=1, CellY=1)");
            Console.WriteLine("  차량용: vehicle_*.ynv");
            Console.WriteLine();
            Console.WriteLine("CodeWalker RPF Explorer 또는 OpenIV 로 추출 후 재실행하세요.");
            return;
        }

        int total = 0, ok = 0, fail = 0;
        int totalPolys = 0, totalVerts = 0, totalPortals = 0, totalPoints = 0;

        foreach (var path in files)
        {
            total++;
            try
            {
                var ynv = YnvFile.Load(path);
                ok++;
                totalPolys   += ynv.Polys.Length;
                totalVerts   += ynv.Vertices.Length;
                totalPortals += ynv.Portals.Length;
                totalPoints  += (int)ynv.PointsCount;

                string typeStr = ynv.IsVehicle ? "Vehicle" : "Static";
                Console.WriteLine($"[{ok:D4}] {Path.GetFileName(path),-22} " +
                    $"area={ynv.AreaID,4}(X={ynv.CellX,2},Y={ynv.CellY,2})  " +
                    $"{typeStr,-8}  " +
                    $"polys={ynv.Polys.Length,5}  verts={ynv.Vertices.Length,5}  " +
                    $"portals={ynv.Portals.Length,4}  pts={ynv.PointsCount,4}");

                if (verbose)
                {
                    Out.Log("  ", "ContentFlags",  ynv.ContentFlags.ToString());
                    Out.Log("  ", "AABBMin",        Out.Vec3(ynv.AABBMin));
                    Out.Log("  ", "AABBSize",       Out.Vec3(ynv.AABBSize));
                    Out.Log("  ", "TotalBytes",     $"{ynv.TotalBytes:N0}");
                    Out.Log("  ", "VersionUnk2",    $"0x{ynv.VersionUnk2:X8}");
                    Out.Log("  ", "AdjAreaIDs",
                        string.Join(", ", ynv.AdjAreaIDs));

                    if (ynv.Vertices.Length > 0)
                    {
                        Console.WriteLine("  Vertices (최초 3개, 월드 좌표):");
                        for (int i = 0; i < Math.Min(3, ynv.Vertices.Length); i++)
                        {
                            var w = ynv.Vertices[i].ToWorld(ynv.AABBMin, ynv.AABBSize);
                            Console.WriteLine($"    [{i}] {Out.Vec3(w)}");
                        }
                    }
                    if (ynv.Polys.Length > 0)
                    {
                        Console.WriteLine("  Polys (최초 5개):");
                        for (int i = 0; i < Math.Min(5, ynv.Polys.Length); i++)
                        {
                            var p = ynv.Polys[i];
                            Console.WriteLine($"    [{i:D4}] verts={p.IndexCount}  " +
                                $"area={p.AreaID}  part={p.PartID}  " +
                                $"flags=[{p.FlagSummary()}]");
                        }
                    }
                    if (ynv.Portals.Length > 0)
                    {
                        Console.WriteLine("  Portals (최초 3개):");
                        for (int i = 0; i < Math.Min(3, ynv.Portals.Length); i++)
                        {
                            var p = ynv.Portals[i];
                            var wF = p.PositionFrom.ToWorld(ynv.AABBMin, ynv.AABBSize);
                            var wT = p.PositionTo.ToWorld(ynv.AABBMin, ynv.AABBSize);
                            Console.WriteLine($"    [{i}] type={p.Type}  " +
                                $"from=poly{p.PolyIDFrom1}(area{p.AreaIDFrom})→" +
                                $"to=poly{p.PolyIDTo1}(area{p.AreaIDTo})  " +
                                $"pos={Out.Vec3(wF)}→{Out.Vec3(wT)}");
                        }
                    }
                    if (ynv.SectorTree != null)
                    {
                        NavMeshParser.CollectSectorStats(ynv.SectorTree,
                            out int leaves, out int pts, out int polyRefs);
                        Console.WriteLine($"  SectorTree: leaves={leaves}  " +
                            $"points={pts}  polyRefs={polyRefs}");
                    }
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {Path.GetFileName(path)}: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"══ 요약 ════════════════════════════════");
        Console.WriteLine($"  총 파일:     {total}개");
        Console.WriteLine($"  성공:         {ok}개");
        if (fail > 0)
            Console.WriteLine($"  실패:         {fail}개");
        Console.WriteLine($"  폴리곤 합계: {totalPolys:N0}개");
        Console.WriteLine($"  버텍스 합계: {totalVerts:N0}개");
        Console.WriteLine($"  포탈 합계:   {totalPortals:N0}개");
        Console.WriteLine($"  포인트 합계: {totalPoints:N0}개");

        Console.Write( "계속하려면 아무키나 누르세요 : " );
        Console.Read();
    }
}

} // namespace YnvReader
