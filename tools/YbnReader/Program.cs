// =============================================================================
// YbnReader — GTA5 .ybn 파일 파서 (학습용, 독립 실행)
//
// 이 파일 하나에 파싱에 필요한 모든 타입과 로직이 담겨 있습니다.
// CodeWalker 의존 없이 동작하며, 파싱 과정을 단계별로 출력합니다.
//
// 실행:  dotnet run                        (기본 디렉토리 사용)
//        dotnet run -- "C:\path\to\dir"    (디렉토리 지정)
//        dotnet run -- "C:\path\to\file.ybn" --verbose  (상세 출력)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace YbnReader
{

// =============================================================================
// ── 1. GTA5 가상 주소 체계 ────────────────────────────────────────────────────
//
//  GTA5 리소스 파일은 '가상 주소(Virtual Address)'를 사용합니다.
//  포인터 필드(u64)를 파일 오프셋으로 바꾸려면 세그먼트 비트를 제거합니다.
//
//    System  segment: 0x5XXXXXXX  → CPU/물리 메모리 (Bounds 등)
//    Graphics segment: 0x6XXXXXXX → GPU 메모리     (.ybn 에서는 미사용)
//
//    파일 오프셋 = ptr & 0x0FFFFFFF
//
//  포인터가 null 이면 최하위 28비트가 전부 0 입니다.
// =============================================================================

static class VA
{
    const uint SYS = 0x50000000;
    const uint GFX = 0x60000000;
    const uint SEG = 0xF0000000;
    const uint OFF = 0x0FFFFFFF;

    // u32로 잘라서 비교 — u64 리터럴 타입 불일치 방지
    static uint Lo32(ulong ptr) => (uint)ptr;

    public static bool IsValid(ulong ptr) =>
        (Lo32(ptr) & OFF) != 0 &&
        ((Lo32(ptr) & SEG) == SYS || (Lo32(ptr) & SEG) == GFX);

    public static int FileOffset(ulong ptr) => (int)(Lo32(ptr) & OFF);

    // 콘솔 출력용 설명 문자열
    public static string Describe(ulong ptr) =>
        IsValid(ptr)
            ? $"0x{ptr:X8}  →  file@0x{FileOffset(ptr):X}"
            : "(null)";
}


// =============================================================================
// ── 2. ResourceReader — byte[] 래퍼 ─────────────────────────────────────────
//
//  GTA5 리소스 데이터는 리틀엔디안 바이너리입니다.
//  ResourceReader 는 파일 오프셋을 받아 기본 타입을 읽어 줍니다.
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

    // XYZ 만 있는 float3 (GTA5 의 많은 벡터 필드)
    public (float X, float Y, float Z) F32x3(int off) =>
        (F32(off), F32(off + 4), F32(off + 8));

    // 포인터를 읽어 파일 오프셋으로 변환
    public int PtrOffset(int off) => VA.FileOffset(U64(off));
}


// =============================================================================
// ── 3. BoundsType 열거형 ────────────────────────────────────────────────────
//
//  모든 Bound 구조체의 첫 타입 바이트(offset 0x10)에 저장됩니다.
//  값에 따라 헤더 뒤에 추가로 읽어야 할 데이터 구조가 달라집니다.
// =============================================================================

enum BoundsType : byte
{
    Sphere      = 0,
    Capsule     = 1,
    Box         = 3,
    Geometry    = 4,    // 폴리곤 메시
    GeometryBVH = 8,    // 폴리곤 메시 + BVH 가속구조
    Composite   = 10,   // 여러 자식 Bound 의 컨테이너
    Disc        = 12,
    Cylinder    = 13,
    Cloth       = 15,
    None        = 255,
}


// =============================================================================
// ── 4. BoundsCommon — 모든 Bound 가 공유하는 헤더 (정확히 0x70 = 112 bytes) ──
//
//  Binary layout (little-endian):
//
//   [0x00] u32  FileVft            — 가상 함수 테이블 포인터 하위 32비트
//   [0x04] u32  FileUnknown
//   [0x08] u64  FilePagesInfoPtr   — 리소스 페이지 정보 포인터 (가상 주소)
//   [0x10] u8   Type               — BoundsType 열거값
//   [0x11] u8   Unknown11
//   [0x12] u16  Unknown12
//   [0x14] f32  SphereRadius       — 바운딩 구 반지름
//   [0x18] u32  Unknown18
//   [0x1C] u32  Unknown1C
//   [0x20] f32x3 BoxMax            — AABB 최대값 (12 bytes)
//   [0x2C] f32  Margin             — 충돌 여유 거리
//   [0x30] f32x3 BoxMin            — AABB 최소값 (12 bytes)
//   [0x3C] u32  UnkType            — 0=normal 1=ybn 2=yft-only
//   [0x40] f32x3 BoxCenter         — AABB 중심 (12 bytes)
//   [0x4C] u8   MaterialIndex
//   [0x4D] u8   ProceduralId
//   [0x4E] u8   RoomIdAndPedDensity — bits[0..4]=RoomId, bits[5..7]=PedDensity
//   [0x4F] u8   UnkFlags
//   [0x50] f32x3 SphereCenter      — 바운딩 구 중심 (12 bytes)
//   [0x5C] u8   PolyFlags
//   [0x5D] u8   MaterialColorIndex
//   [0x5E] u16  Unknown5E
//   [0x60] f32x3 Inertia           — 관성 텐서 (12 bytes)
//   [0x6C] f32  Volume
// =============================================================================

class BoundsCommon
{
    public int FileOffset;

    // ResourceFileBase
    public uint  FileVft;
    public ulong FilePagesInfoPtr;

    // 공통 물리 속성
    public BoundsType Type;
    public float  SphereRadius;
    public (float X, float Y, float Z) BoxMax;
    public (float X, float Y, float Z) BoxMin;
    public (float X, float Y, float Z) BoxCenter;
    public (float X, float Y, float Z) SphereCenter;
    public float  Margin;
    public float  Volume;
    public (float X, float Y, float Z) Inertia;

    // 재질 / 식별자
    public byte  MaterialIndex;
    public byte  ProceduralId;
    public byte  RoomIdAndPedDensity;
    public uint  UnkType;   // 0=normal, 1=ybn, 2=yft-only

    public static BoundsCommon Read(ResourceReader r, int off)
    {
        return new BoundsCommon
        {
            FileOffset          = off,
            FileVft             = r.U32(off + 0x00),
            FilePagesInfoPtr    = r.U64(off + 0x08),
            Type                = (BoundsType)r.U8(off + 0x10),
            SphereRadius        = r.F32(off + 0x14),
            BoxMax              = r.F32x3(off + 0x20),
            Margin              = r.F32(off + 0x2C),
            BoxMin              = r.F32x3(off + 0x30),
            UnkType             = r.U32(off + 0x3C),
            BoxCenter           = r.F32x3(off + 0x40),
            MaterialIndex       = r.U8(off + 0x4C),
            ProceduralId        = r.U8(off + 0x4D),
            RoomIdAndPedDensity = r.U8(off + 0x4E),
            SphereCenter        = r.F32x3(off + 0x50),
            Inertia             = r.F32x3(off + 0x60),
            Volume              = r.F32(off + 0x6C),
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad,"BoundsCommon", $"file@0x{FileOffset:X}  (0x70 bytes)");
        Out.Log(pad,"  [0x00] FileVft", $"0x{FileVft:X8}");
        Out.Log(pad,"  [0x08] PagesInfoPtr", VA.Describe(FilePagesInfoPtr));
        Out.Log(pad,"  [0x10] Type", $"{Type} ({(byte)Type})");
        Out.Log(pad,"  [0x14] SphereRadius", $"{SphereRadius:F4}");
        Out.Log(pad,"  [0x20] BoxMax", Vec3(BoxMax));
        Out.Log(pad,"  [0x30] BoxMin", Vec3(BoxMin));
        Out.Log(pad,"  [0x2C] Margin", $"{Margin:F4}");
        Out.Log(pad,"  [0x6C] Volume", $"{Volume:F4}");
        if (verbose)
        {
            Out.Log(pad,"  [0x40] BoxCenter", Vec3(BoxCenter));
            Out.Log(pad,"  [0x50] SphereCenter", Vec3(SphereCenter));
            Out.Log(pad,"  [0x60] Inertia", Vec3(Inertia));
            Out.Log(pad,"  [0x4C] MaterialIndex", $"{MaterialIndex}");
            Out.Log(pad,"  [0x4D] ProceduralId", $"{ProceduralId}");
            Out.Log(pad,"  [0x3C] UnkType", $"{UnkType}");
        }
    }

    static string Vec3((float X, float Y, float Z) v) =>
        $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}


// =============================================================================
// ── 5a. BoundCompositeData — Composite 전용 추가 데이터 (0x40 bytes @ 0x70) ─
//
//   [0x70] u64  ChildrenPointer               — 자식 포인터 배열
//   [0x78] u64  ChildrenTransformation1Ptr    — 변환 행렬 배열 1
//   [0x80] u64  ChildrenTransformation2Ptr    — 변환 행렬 배열 2
//   [0x88] u64  ChildrenBoundingBoxesPtr      — 자식 AABB 배열
//   [0x90] u64  ChildrenFlags1Ptr             — 자식 충돌 플래그 배열 1
//   [0x98] u64  ChildrenFlags2Ptr             — 자식 충돌 플래그 배열 2
//   [0xA0] u16  ChildrenCount1
//   [0xA2] u16  ChildrenCount2
//   [0xA4] u32  Unknown_A4
//   [0xA8] u64  BVHPointer                   — 복합 BVH 포인터
// =============================================================================

class BoundCompositeData
{
    public int FileOffset;

    public ulong ChildrenPointer;
    public ulong ChildrenTransformation1Ptr;
    public ulong ChildrenTransformation2Ptr;
    public ulong ChildrenBoundingBoxesPtr;
    public ulong ChildrenFlags1Ptr;
    public ulong ChildrenFlags2Ptr;
    public ushort ChildrenCount1;
    public ushort ChildrenCount2;
    public ulong BVHPointer;

    public static BoundCompositeData Read(ResourceReader r, int baseOff)
    {
        // baseOff = BoundsCommon 시작 위치
        // Composite 전용 데이터는 +0x70 부터 시작
        int b = baseOff + 0x70;
        return new BoundCompositeData
        {
            FileOffset                  = b,
            ChildrenPointer             = r.U64(b + 0x00), // 0x70
            ChildrenTransformation1Ptr  = r.U64(b + 0x08), // 0x78
            ChildrenTransformation2Ptr  = r.U64(b + 0x10), // 0x80
            ChildrenBoundingBoxesPtr    = r.U64(b + 0x18), // 0x88
            ChildrenFlags1Ptr           = r.U64(b + 0x20), // 0x90
            ChildrenFlags2Ptr           = r.U64(b + 0x28), // 0x98
            ChildrenCount1              = r.U16(b + 0x30), // 0xA0
            ChildrenCount2              = r.U16(b + 0x32), // 0xA2
            BVHPointer                  = r.U64(b + 0x38), // 0xA8
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad,"BoundCompositeData", $"file@0x{FileOffset:X}  (0x40 bytes)");
        Out.Log(pad,"  [0x70] ChildrenPointer", VA.Describe(ChildrenPointer));
        Out.Log(pad,"  [0xA0] ChildrenCount", $"{ChildrenCount1}  (count2={ChildrenCount2})");
        Out.Log(pad,"  [0xA8] BVHPointer", VA.Describe(BVHPointer));
        if (verbose)
        {
            Out.Log(pad,"  [0x78] Transformation1Ptr", VA.Describe(ChildrenTransformation1Ptr));
            Out.Log(pad,"  [0x80] Transformation2Ptr", VA.Describe(ChildrenTransformation2Ptr));
            Out.Log(pad,"  [0x88] BoundingBoxesPtr", VA.Describe(ChildrenBoundingBoxesPtr));
            Out.Log(pad,"  [0x90] Flags1Ptr", VA.Describe(ChildrenFlags1Ptr));
            Out.Log(pad,"  [0x98] Flags2Ptr", VA.Describe(ChildrenFlags2Ptr));
        }
    }
}


// =============================================================================
// ── 5b. BoundGeometryData — Geometry/GeometryBVH 전용 (0xC0 bytes @ 0x70) ──
//
//   [0x70] u32  Unknown70
//   [0x74] u32  Unknown74
//   [0x78] u64  VerticesShrunkPointer   — 압축 정점 배열
//   [0x80] u16  Unknown80
//   [0x82] u16  Unknown82
//   [0x84] u32  VerticesShrunkCount
//   [0x88] u64  PolygonsPointer         — 폴리곤 배열
//   [0x90] f32x3 Quantum                — 정점 압축 스케일
//   [0x9C] f32  UnknownQuantumW
//   [0xA0] f32x3 CenterGeom             — 정점 압축 기준점
//   [0xAC] f32  UnknownCenterW
//   [0xB0] u64  VerticesPointer         — 정점 배열
//   [0xB8] u64  VertexColoursPointer
//   [0xC0] u64  OctantsPointer          — 옥트리 분할 정보
//   [0xC8] u64  OctantItemsPointer
//   [0xD0] u32  VerticesCount
//   [0xD4] u32  PolygonsCount
//   ... (4x u32 reserved) ...
//   [0xF0] u64  MaterialsPointer
//   [0xF8] u64  MaterialColoursPointer
//   ... (6x u32 reserved) ...
//   [0x118] u64  PolygonMaterialIndicesPointer
//   [0x120] u8   MaterialsCount
//   [0x121] u8   MaterialColoursCount
// =============================================================================

class BoundGeometryData
{
    public int FileOffset;

    // 정점 / 폴리곤
    public ulong VerticesShrunkPointer;
    public uint  VerticesShrunkCount;
    public ulong PolygonsPointer;
    public ulong VerticesPointer;
    public ulong VertexColoursPointer;
    public uint  VerticesCount;
    public uint  PolygonsCount;

    // 옥트리
    public ulong OctantsPointer;
    public ulong OctantItemsPointer;

    // 재질
    public ulong MaterialsPointer;
    public ulong MaterialColoursPointer;
    public ulong PolygonMaterialIndicesPointer;
    public byte  MaterialsCount;
    public byte  MaterialColoursCount;

    // 정점 압축 파라미터
    public (float X, float Y, float Z) Quantum;    // 스케일
    public (float X, float Y, float Z) CenterGeom; // 오프셋

    public static BoundGeometryData Read(ResourceReader r, int baseOff)
    {
        // baseOff = BoundsCommon 시작. Geometry 데이터는 동일 위치에서 오프셋으로 접근
        return new BoundGeometryData
        {
            FileOffset                      = baseOff + 0x70,
            VerticesShrunkPointer           = r.U64(baseOff + 0x78),
            VerticesShrunkCount             = r.U32(baseOff + 0x84),
            PolygonsPointer                 = r.U64(baseOff + 0x88),
            Quantum                         = r.F32x3(baseOff + 0x90),
            CenterGeom                      = r.F32x3(baseOff + 0xA0),
            VerticesPointer                 = r.U64(baseOff + 0xB0),
            VertexColoursPointer            = r.U64(baseOff + 0xB8),
            OctantsPointer                  = r.U64(baseOff + 0xC0),
            OctantItemsPointer              = r.U64(baseOff + 0xC8),
            VerticesCount                   = r.U32(baseOff + 0xD0),
            PolygonsCount                   = r.U32(baseOff + 0xD4),
            MaterialsPointer                = r.U64(baseOff + 0xF0),
            MaterialColoursPointer          = r.U64(baseOff + 0xF8),
            PolygonMaterialIndicesPointer   = r.U64(baseOff + 0x118),
            MaterialsCount                  = r.U8 (baseOff + 0x120),
            MaterialColoursCount            = r.U8 (baseOff + 0x121),
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad,"BoundGeometryData", $"file@0x{FileOffset:X}  (0xC0 bytes)");
        Out.Log(pad,"  [0xD0] VerticesCount",  $"{VerticesCount}");
        Out.Log(pad,"  [0xD4] PolygonsCount",  $"{PolygonsCount}");
        Out.Log(pad,"  [0x120] MaterialsCount", $"{MaterialsCount}  colours={MaterialColoursCount}");
        if (verbose)
        {
            Out.Log(pad,"  [0x78] VerticesShrunkPtr", VA.Describe(VerticesShrunkPointer));
            Out.Log(pad,"  [0x84] VerticesShrunkCount", $"{VerticesShrunkCount}");
            Out.Log(pad,"  [0x88] PolygonsPtr",  VA.Describe(PolygonsPointer));
            Out.Log(pad,"  [0xB0] VerticesPtr",  VA.Describe(VerticesPointer));
            Out.Log(pad,"  [0xC0] OctantsPtr",   VA.Describe(OctantsPointer));
            Out.Log(pad,"  [0xF0] MaterialsPtr", VA.Describe(MaterialsPointer));
            Out.Log(pad,"  [0x90] Quantum",  $"({Quantum.X:F4}, {Quantum.Y:F4}, {Quantum.Z:F4})");
            Out.Log(pad,"  [0xA0] Center",   $"({CenterGeom.X:F4}, {CenterGeom.Y:F4}, {CenterGeom.Z:F4})");
        }
    }
}


// =============================================================================
// ── 5c. BoundBVHData — GeometryBVH 전용 추가 데이터 (0x20 bytes @ 0x130) ───
//
//   [0x130] u64  BvhPointer   — BVH 블록을 가리키는 포인터
//   [0x138] u32  Unknown138
//   [0x13C] u32  Unknown13C
//   (이후 추가 u16/u32 필드들)
// =============================================================================

class BoundBVHData
{
    public int   FileOffset;
    public ulong BvhPointer;
    public uint  Unknown138;
    public uint  Unknown13C;

    public static BoundBVHData Read(ResourceReader r, int baseOff)
    {
        int b = baseOff + 0x130;
        return new BoundBVHData
        {
            FileOffset   = b,
            BvhPointer   = r.U64(b + 0x00), // 0x130
            Unknown138   = r.U32(b + 0x08), // 0x138
            Unknown13C   = r.U32(b + 0x0C), // 0x13C
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad,"BoundBVHData", $"file@0x{FileOffset:X}  (0x20 bytes)");
        Out.Log(pad,"  [0x130] BvhPointer", VA.Describe(BvhPointer));
    }
}


// =============================================================================
// ── 5d. BoundsTail16 — Capsule/Disc/Cylinder 전용 (0x10 bytes @ 0x70) ──────
//
//  이 세 타입은 헤더(0x70) 바로 뒤에 16바이트 추가 데이터만 붙습니다.
// =============================================================================

class BoundsTail16
{
    public uint A, B, C, D;

    public static BoundsTail16 Read(ResourceReader r, int baseOff)
    {
        int b = baseOff + 0x70;
        return new BoundsTail16
        {
            A = r.U32(b + 0x00),
            B = r.U32(b + 0x04),
            C = r.U32(b + 0x08),
            D = r.U32(b + 0x0C),
        };
    }
}


// =============================================================================
// ── 6. BoundNode — 타입 판별 및 재귀 파싱 ──────────────────────────────────
//
//  파싱 흐름:
//    1. file@offset 에서 BoundsCommon 읽기 (항상 0x70 bytes)
//    2. Common.Type 에 따라 분기:
//       Composite   → BoundCompositeData  (@ baseOff+0x70)
//                     자식 포인터 배열을 순회하며 재귀 파싱
//       Geometry    → BoundGeometryData   (@ baseOff+0x70~0x12F)
//       GeometryBVH → BoundGeometryData + BoundBVHData
//       Capsule/Disc/Cylinder → BoundsTail16 (@ baseOff+0x70)
//       Sphere/Box/Cloth → BoundsCommon 만으로 완결
// =============================================================================

class BoundNode
{
    public BoundsCommon     Common     = null!;
    public BoundCompositeData? Composite;
    public BoundGeometryData?  Geometry;
    public BoundBVHData?       BVH;
    public BoundsTail16?       Tail16;
    public List<BoundNode>  Children = new();

    // 파일 오프셋에서 Bound 하나를 읽고 BoundNode 를 반환합니다.
    // depth: 재귀 깊이 제한 (순환 방지)
    public static BoundNode Read(ResourceReader r, int off, int depth = 0)
    {
        var node = new BoundNode();

        // ── 단계 1: 공통 헤더 읽기 ──────────────────────────────────────────
        node.Common = BoundsCommon.Read(r, off);

        // ── 단계 2: 타입별 추가 데이터 읽기 ─────────────────────────────────
        switch (node.Common.Type)
        {
            case BoundsType.Composite:
                node.Composite = BoundCompositeData.Read(r, off);

                // ── 단계 3(Composite 한정): 자식 포인터 배열 순회 ─────────────
                // ChildrenPointer 는 u64 가상주소 배열을 가리킴
                // 각 원소를 파일 오프셋으로 변환해 재귀 파싱
                if (depth < 4 && VA.IsValid(node.Composite.ChildrenPointer))
                {
                    int arrayOff = VA.FileOffset(node.Composite.ChildrenPointer);
                    for (int i = 0; i < node.Composite.ChildrenCount1; i++)
                    {
                        ulong childPtr = r.U64(arrayOff + i * 8);
                        if (VA.IsValid(childPtr))
                        {
                            var child = BoundNode.Read(r, VA.FileOffset(childPtr), depth + 1);
                            node.Children.Add(child);
                        }
                    }
                }
                break;

            case BoundsType.Geometry:
                node.Geometry = BoundGeometryData.Read(r, off);
                break;

            case BoundsType.GeometryBVH:
                node.Geometry = BoundGeometryData.Read(r, off);
                node.BVH      = BoundBVHData.Read(r, off);
                break;

            case BoundsType.Capsule:
            case BoundsType.Disc:
            case BoundsType.Cylinder:
                node.Tail16 = BoundsTail16.Read(r, off);
                break;

            // Sphere / Box / Cloth: BoundsCommon 만으로 완결
        }

        return node;
    }

    // 트리 형태로 출력
    public void Print(string pad = "", bool verbose = false)
    {
        Common.Print(pad, verbose);
        Composite?.Print(pad, verbose);
        Geometry?.Print(pad, verbose);
        BVH?.Print(pad, verbose);

        if (Children.Count > 0)
        {
            Console.WriteLine($"{pad}  ┌─ Children ({Children.Count})");
            for (int i = 0; i < Children.Count; i++)
            {
                bool last = i == Children.Count - 1;
                string branch = last ? "└─" : "├─";
                Console.WriteLine($"{pad}  {branch} [{i}] {Children[i].Common.Type}  file@0x{Children[i].Common.FileOffset:X}");
                Children[i].Print(pad + (last ? "     " : "  │  "), verbose);
            }
        }
    }

    // 요약 한 줄
    public string Summary()
    {
        string extra = Common.Type switch
        {
            BoundsType.Composite   => $"  children={Composite?.ChildrenCount1 ?? 0}",
            BoundsType.Geometry    => $"  verts={Geometry?.VerticesCount}  polys={Geometry?.PolygonsCount}",
            BoundsType.GeometryBVH => $"  verts={Geometry?.VerticesCount}  polys={Geometry?.PolygonsCount}  bvh={VA.Describe(BVH?.BvhPointer ?? 0)}",
            _                      => "",
        };
        return $"{Common.Type,-12} file@0x{Common.FileOffset:X8}  r={Common.SphereRadius:F3}{extra}";
    }
}


// =============================================================================
// ── 7. YbnFile — .ybn 파일 하나를 로드 ──────────────────────────────────────
//
//  .ybn 파일 형식 (압축 해제 완료 상태):
//
//    bytes 0..N-1 : 시스템 세그먼트 전체
//                   offset 0 = 가상주소 0x50000000
//
//  RSC7 헤더(magic = 0x37435352)가 붙은 압축 파일은 이 도구에서 미지원.
//  (CodeWalker 의 ResourceBuilder.Decompress 를 먼저 거쳐야 함)
//
//  오프셋 0 에서 BoundNode 를 바로 읽기 시작합니다.
//  파일 첫 4바이트가 VFT 하위 32비트이므로 RSC7 magic 과 구분됩니다.
// =============================================================================

class YbnFile
{
    const uint RSC7_MAGIC = 0x37435352; // 'RSC7'

    public string    FilePath = "";
    public int       FileSize;
    public BoundNode Root = null!;

    public static YbnFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r = new ResourceReader(data);

        uint magic = r.U32(0);
        if (magic == RSC7_MAGIC)
            throw new InvalidDataException("RSC7 압축 파일 — 이 도구는 압축 해제된 .ybn 만 지원합니다.");

        return new YbnFile
        {
            FilePath = path,
            FileSize = data.Length,
            Root     = BoundNode.Read(r, 0),
        };
    }
}


// =============================================================================
// ── 8. 공통 출력 헬퍼 ────────────────────────────────────────────────────────
// =============================================================================

static class Out
{
    public static void Log(string pad, string field, string value) =>
        Console.WriteLine($"{pad}  {field,-32} {value}");
}


// =============================================================================
// ── 9. 진입점 ────────────────────────────────────────────────────────────────
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

        // 단일 파일 vs 디렉토리
        string[] files = File.Exists(target)
            ? [target]
            : Directory.GetFiles(target, "*.ybn", SearchOption.AllDirectories);

        Console.WriteLine($".ybn 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"{'═',1}{'═',-60} {Path.GetFileName(path)}");
            try
            {
                var ybn = YbnFile.Load(path);
                ok++;

                // ── 상세 출력 (verbose 또는 파일 1개 지정 시) ─────────────────
                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  파일 크기: {ybn.FileSize:N0} bytes");
                    Console.WriteLine();
                    ybn.Root.Print("  ", verbose);
                }
                else
                {
                    // ── 요약 출력 ─────────────────────────────────────────────
                    Console.WriteLine($"  루트: {ybn.Root.Summary()}");
                    foreach (var child in ybn.Root.Children)
                        Console.WriteLine($"    자식: {child.Summary()}");
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

} // namespace YbnReader
