// =============================================================================
// YdrReader — GTA5 .ydr 파일 파서 (학습용, 독립 실행)
//
// 이 파일 하나에 파싱에 필요한 모든 타입과 로직이 담겨 있습니다.
// CodeWalker 의존 없이 동작하며, 파싱 과정을 단계별로 출력합니다.
//
// 실행:  dotnet run                           (기본 디렉토리 사용)
//        dotnet run -- "C:\path\to\dir"       (디렉토리 지정)
//        dotnet run -- "C:\path\to\file.ydr" --verbose  (상세 출력)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YdrReader
{

// =============================================================================
// ── 1. GTA5 가상 주소 체계 ────────────────────────────────────────────────────
//
//  GTA5 리소스 파일은 '가상 주소(Virtual Address)'를 사용합니다.
//  포인터 필드(u64)를 파일 오프셋으로 바꾸려면 세그먼트 비트를 제거합니다.
//
//    System   segment: 0x5XXXXXXX  → CPU/물리 메모리 (Drawable 등)
//    Graphics segment: 0x6XXXXXXX  → GPU 메모리 (Vertex/Index 버퍼 등)
//
//    파일 오프셋 = ptr & 0x0FFFFFFF
//    .ydr raw 파일: [시스템 세그먼트][그래픽 세그먼트] 순서로 저장
//
//    이 도구는 시스템 세그먼트만 파싱합니다.
//    그래픽 세그먼트(버퍼 데이터)는 별도 기준 오프셋이 필요해 스킵합니다.
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

    public static bool IsSys(ulong ptr) =>
        (Lo32(ptr) & SEG) == SYS && (Lo32(ptr) & OFF) != 0;

    public static int FileOffset(ulong ptr) => (int)(Lo32(ptr) & OFF);

    public static string Describe(ulong ptr) =>
        IsValid(ptr)
            ? $"0x{ptr:X8}  →  file@0x{FileOffset(ptr):X}"
            : "(null)";
}


// =============================================================================
// ── 2. ResourceReader — byte[] 래퍼 ─────────────────────────────────────────
// =============================================================================

class ResourceReader
{
    readonly byte[] _data;

    public ResourceReader(byte[] data) => _data = data;

    public int    Length       => _data.Length;
    public byte   U8 (int off) => off < _data.Length ? _data[off] : (byte)0;
    public ushort U16(int off) => off + 2 <= _data.Length ? BitConverter.ToUInt16(_data, off) : (ushort)0;
    public uint   U32(int off) => off + 4 <= _data.Length ? BitConverter.ToUInt32(_data, off) : 0u;
    public ulong  U64(int off) => off + 8 <= _data.Length ? BitConverter.ToUInt64(_data, off) : 0ul;
    public float  F32(int off) => off + 4 <= _data.Length ? BitConverter.ToSingle(_data, off) : 0f;

    public (float X, float Y, float Z) F32x3(int off) =>
        (F32(off), F32(off + 4), F32(off + 8));

    // 범위 내에서 null-terminated ASCII 문자열 읽기
    public string AsciiStr(int off, int maxLen = 256)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxLen && off + i < _data.Length; i++)
        {
            byte b = _data[off + i];
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}


// =============================================================================
// ── 3. DrawableGeometryInfo — 지오메트리 하나의 핵심 정보 ────────────────────
//
//  DrawableGeometry 헤더 (0x98 = 152 bytes, file@ geomOff):
//
//   [0x00] u32  Vft
//   [0x04] u32  Unknown4
//   [0x08] u64  Unknown8
//   [0x10] u64  Unknown10
//   [0x18] u64  VertexBufferPointer   — 정점 버퍼 (그래픽 세그먼트)
//   [0x20] u64  Unknown20
//   [0x28] u64  Unknown28
//   [0x30] u64  Unknown30
//   [0x38] u64  IndexBufferPointer    — 인덱스 버퍼 (그래픽 세그먼트)
//   [0x40] u64  Unknown40
//   [0x48] u64  Unknown48
//   [0x50] u64  Unknown50
//   [0x58] u32  IndicesCount
//   [0x5C] u32  TrianglesCount
//   [0x60] u16  VerticesCount
//   [0x62] u16  Unknown62
//   [0x64] u32  Unknown64
//   [0x68] u64  BoneIdsPointer
//   [0x70] u16  VertexStride          — 정점 하나의 크기 (bytes)
//   [0x72] u16  BoneIdsCount
//   [0x74] u32  Unknown74
//   [0x78] u64  VertexDataPointer
//   [0x80..0x97] u64×3 Unknown
//
//  DrawableGeometryRaw: 헤더 뒤에 inline BoneIds 배열이 붙음
//   [헤더+0x00] (u64 pad if BoneIdsCount > 4)
//   [헤더+...] u16 BoneIds[BoneIdsCount]
// =============================================================================

class DrawableGeometryInfo
{
    public int FileOffset;

    public ulong VertexBufferPointer;
    public ulong IndexBufferPointer;
    public uint  IndicesCount;
    public uint  TrianglesCount;
    public ushort VerticesCount;
    public ushort VertexStride;
    public ushort BoneIdsCount;
    public ulong BoneIdsPointer;
    public ulong VertexDataPointer;

    public static DrawableGeometryInfo Read(ResourceReader r, int off)
    {
        return new DrawableGeometryInfo
        {
            FileOffset          = off,
            VertexBufferPointer = r.U64(off + 0x18),
            IndexBufferPointer  = r.U64(off + 0x38),
            IndicesCount        = r.U32(off + 0x58),
            TrianglesCount      = r.U32(off + 0x5C),
            VerticesCount       = r.U16(off + 0x60),
            BoneIdsPointer      = r.U64(off + 0x68),
            VertexStride        = r.U16(off + 0x70),
            BoneIdsCount        = r.U16(off + 0x72),
            VertexDataPointer   = r.U64(off + 0x78),
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad, "DrawableGeometry", $"file@0x{FileOffset:X}  (0x98 bytes)");
        Out.Log(pad, "  [0x60] VerticesCount",  $"{VerticesCount}");
        Out.Log(pad, "  [0x5C] TrianglesCount", $"{TrianglesCount}");
        Out.Log(pad, "  [0x70] VertexStride",   $"{VertexStride} bytes");
        Out.Log(pad, "  [0x72] BoneIdsCount",   $"{BoneIdsCount}");
        if (verbose)
        {
            Out.Log(pad, "  [0x58] IndicesCount",        $"{IndicesCount}");
            Out.Log(pad, "  [0x18] VertexBufferPtr",     VA.Describe(VertexBufferPointer));
            Out.Log(pad, "  [0x38] IndexBufferPtr",      VA.Describe(IndexBufferPointer));
            Out.Log(pad, "  [0x68] BoneIdsPtr",          VA.Describe(BoneIdsPointer));
            Out.Log(pad, "  [0x78] VertexDataPtr",       VA.Describe(VertexDataPointer));
        }
    }

    public string Summary() =>
        $"verts={VerticesCount,-5} tris={TrianglesCount,-6} stride={VertexStride,-3} boneIds={BoneIdsCount}";
}


// =============================================================================
// ── 4. DrawableModelInfo — LOD 모델 하나 (DrawableModelRaw) ──────────────────
//
//  DrawableModel 헤더 (0x30 = 48 bytes, file@ modelOff):
//
//   [0x00] u32  Vft
//   [0x04] u32  Unknown4
//   [0x08] u64  GeometriesPointer    — 지오메트리 포인터 배열의 RSC 포인터
//   [0x10] u16  GeometriesCount1     — 지오메트리 수
//   [0x12] u16  GeometriesCount2
//   [0x14] u32  Unknown14
//   [0x18] u64  BoundsPointer        — AABB 배열의 RSC 포인터
//   [0x20] u64  ShaderMappingPointer — 셰이더 매핑 배열의 RSC 포인터
//   [0x28] u32  SkeletonBinding
//   [0x2C] u16  RenderMaskFlags      — 렌더 마스크 (LOD 비트 포함)
//   [0x2E] u16  GeometriesCount3
//
//  헤더의 RSC 포인터를 따라가서 각 배열을 읽습니다.
//  (CodeWalker: ReadUlongsAt(GeometriesPointer, count), ReadUshortsAt(ShaderMappingPointer, count))
// =============================================================================

class DrawableModelInfo
{
    public int FileOffset;

    public ushort GeometriesCount;
    public byte   RenderMask;
    public byte   LodFlags;
    public byte   BoneIndex;
    public byte   HasSkin;

    public List<DrawableGeometryInfo> Geometries = new();

    public static DrawableModelInfo? Read(ResourceReader r, int off)
    {
        uint  vft   = r.U32(off + 0x00);
        if (vft == 0) return null;

        int    count    = r.U16(off + 0x10);
        uint   skel     = r.U32(off + 0x28);
        ushort rmask    = r.U16(off + 0x2C);
        ulong  geomsPtr = r.U64(off + 0x08);  // GeometriesPointer

        var m = new DrawableModelInfo
        {
            FileOffset      = off,
            GeometriesCount = (ushort)count,
            RenderMask      = (byte)(rmask & 0xFF),
            LodFlags        = (byte)((rmask >> 8) & 0xFF),
            BoneIndex       = (byte)((skel >> 24) & 0xFF),
            HasSkin         = (byte)((skel >> 8) & 0xFF),
        };

        if (!VA.IsSys(geomsPtr) || count == 0) return m;

        // GeometriesPointer → RSC 포인터 배열 따라가기
        int geomPtrOff = VA.FileOffset(geomsPtr);

        for (int i = 0; i < count; i++)
        {
            ulong geomPtr = r.U64(geomPtrOff + i * 8);
            if (!VA.IsSys(geomPtr)) continue;

            int geomOff = VA.FileOffset(geomPtr);
            if (geomOff + 0x98 > r.Length) continue;

            m.Geometries.Add(DrawableGeometryInfo.Read(r, geomOff));
        }

        return m;
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad, "DrawableModel", $"file@0x{FileOffset:X}  geoms={GeometriesCount}");
        Out.Log(pad, "  [0x10] GeometriesCount", $"{GeometriesCount}");
        Out.Log(pad, "  [0x2C] RenderMask",      $"0x{RenderMask:X2}  flags=0x{LodFlags:X2}");
        if (verbose)
        {
            Out.Log(pad, "  [0x28] BoneIndex",   $"{BoneIndex}  hasSkin={HasSkin}");
        }
        foreach (var g in Geometries)
            g.Print(pad + "    ", verbose);
    }

    public string Summary() =>
        $"geoms={GeometriesCount,-2} renderMask=0x{RenderMask:X2}" +
        (Geometries.Count > 0 ? $"  [{Geometries[0].Summary()}]" : "");
}


// =============================================================================
// ── 5. DrawableModelListInfo — LOD 레벨 하나 (DrawableModelPointerList) ───────
//
//  ResourcePointerListHeader (0x10 = 16 bytes):
//   [0x00] u64  Pointer    — u64 포인터 배열을 가리킴
//   [0x08] u16  Count
//   [0x0A] u16  Capacity
//   [0x0C] u32  Unknown
//
//  Pointer → u64 ModelPointers[Capacity]  (각 → DrawableModelRaw)
// =============================================================================

class DrawableModelListInfo
{
    public int FileOffset;
    public ushort Count;
    public ushort Capacity;

    public List<DrawableModelInfo> Models = new();

    public static DrawableModelListInfo? Read(ResourceReader r, int off)
    {
        if (off + 0x10 > r.Length) return null;

        ulong  ptr  = r.U64(off + 0x00);
        ushort cnt  = r.U16(off + 0x08);
        ushort cap  = r.U16(off + 0x0A);

        var list = new DrawableModelListInfo
        {
            FileOffset = off,
            Count      = cnt,
            Capacity   = cap,
        };

        if (!VA.IsSys(ptr) || cap == 0) return list;

        int ptrArrayOff = VA.FileOffset(ptr);
        for (int i = 0; i < cap; i++)
        {
            ulong modelPtr = r.U64(ptrArrayOff + i * 8);
            if (!VA.IsSys(modelPtr)) continue;

            int modelOff = VA.FileOffset(modelPtr);
            if (modelOff + 0x30 > r.Length) continue;

            var m = DrawableModelInfo.Read(r, modelOff);
            if (m != null) list.Models.Add(m);
        }

        return list;
    }
}


// =============================================================================
// ── 6. ShaderGroupInfo — ShaderGroup 헤더 ────────────────────────────────────
//
//   [0x00] u32  Vft
//   [0x04] u32  Unknown4
//   [0x08] u64  TextureDictionaryPointer
//   [0x10] u64  ShadersPointer
//   [0x18] u16  ShadersCount1
//   [0x1A] u16  ShadersCount2
//   [0x1C] u32  Unknown1C
//   [0x20] u64  Unknown20
//   [0x28] u64  Unknown28
//   [0x30] u32  ShaderGroupBlocksSize
//   [0x34] u32  Unknown34
//   [0x38] u64  Unknown38
// =============================================================================

class ShaderGroupInfo
{
    public int FileOffset;

    public ulong  TextureDictionaryPointer;
    public ulong  ShadersPointer;
    public ushort ShadersCount1;
    public ushort ShadersCount2;
    public uint   ShaderGroupBlocksSize;

    public static ShaderGroupInfo Read(ResourceReader r, int off)
    {
        return new ShaderGroupInfo
        {
            FileOffset                 = off,
            TextureDictionaryPointer   = r.U64(off + 0x08),
            ShadersPointer             = r.U64(off + 0x10),
            ShadersCount1              = r.U16(off + 0x18),
            ShadersCount2              = r.U16(off + 0x1A),
            ShaderGroupBlocksSize      = r.U32(off + 0x30),
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad, "ShaderGroup", $"file@0x{FileOffset:X}");
        Out.Log(pad, "  [0x18] ShadersCount",  $"{ShadersCount1}");
        Out.Log(pad, "  [0x30] BlocksSize",    $"0x{ShaderGroupBlocksSize:X}");
        if (verbose)
        {
            Out.Log(pad, "  [0x08] TexDictPtr",   VA.Describe(TextureDictionaryPointer));
            Out.Log(pad, "  [0x10] ShadersPtr",   VA.Describe(ShadersPointer));
        }
    }
}


// =============================================================================
// ── 7. SkeletonInfo — Skeleton 헤더 ──────────────────────────────────────────
//
//   [0x00] u32  Vft
//   [0x04] u32  Unknown4
//   [0x08] u64  Unknown8
//   [0x10] u64  BoneTagsPointer
//   [0x18] u16  BoneTagsCapacity
//   [0x1A] u16  BoneTagsCount
//   [0x1C] u32  Unknown1C
//   [0x20] u64  BonesPointer
//   [0x28] u64  TransformationsInvPointer
//   [0x30] u64  TransformationsPointer
//   [0x38] u64  ParentIndicesPointer
//   [0x40] u64  ChildIndicesPointer
//   [0x48] u64  Unknown48
//   [0x50..0x67] u32×6 Unknown
//   [0x5E] u16  BonesCount
//   [0x60] u16  ChildIndicesCount
//   [0x68] u64  Unknown68
// =============================================================================

class SkeletonInfo
{
    public int FileOffset;

    public ushort BonesCount;
    public ushort BoneTagsCount;
    public ushort ChildIndicesCount;
    public ulong  BonesPointer;
    public ulong  BoneTagsPointer;

    public static SkeletonInfo Read(ResourceReader r, int off)
    {
        return new SkeletonInfo
        {
            FileOffset        = off,
            BoneTagsPointer   = r.U64(off + 0x10),
            BoneTagsCount     = r.U16(off + 0x1A),
            BonesPointer      = r.U64(off + 0x20),
            BonesCount        = r.U16(off + 0x5E),
            ChildIndicesCount = r.U16(off + 0x60),
        };
    }

    public void Print(string pad, bool verbose)
    {
        Out.Log(pad, "Skeleton", $"file@0x{FileOffset:X}");
        Out.Log(pad, "  [0x5E] BonesCount",        $"{BonesCount}");
        Out.Log(pad, "  [0x1A] BoneTagsCount",     $"{BoneTagsCount}");
        Out.Log(pad, "  [0x60] ChildIndicesCount", $"{ChildIndicesCount}");
        if (verbose)
        {
            Out.Log(pad, "  [0x10] BoneTagsPtr", VA.Describe(BoneTagsPointer));
            Out.Log(pad, "  [0x20] BonesPtr",    VA.Describe(BonesPointer));
        }
    }
}


// =============================================================================
// ── 8. DrawableFileInfo — .ydr 파일 전체 파싱 결과 ───────────────────────────
//
//  DrawableBase 레이아웃 (0xA8 = 168 bytes, file@0x00):
//
//   ResourceFileBase (@ 0x00):
//    [0x00] u32  FileVft
//    [0x04] u32  FileUnknown
//    [0x08] u64  FilePagesInfoPointer
//
//   DrawableBase 계속:
//    [0x10] u64  ShaderGroupPointer
//    [0x18] u64  SkeletonPointer
//    [0x20] f32x3 BoundingCenter      (12 bytes)
//    [0x2C] f32  BoundingSphereRadius
//    [0x30] f32x3 BoundingBoxMin      (12 bytes)
//    [0x3C] u32  Unknown3C
//    [0x40] f32x3 BoundingBoxMax      (12 bytes)
//    [0x4C] u32  Unknown4C
//    [0x50] u64  DrawableModelsHighPointer
//    [0x58] u64  DrawableModelsMediumPointer
//    [0x60] u64  DrawableModelsLowPointer
//    [0x68] u64  DrawableModelsVeryLowPointer
//    [0x70] f32  LodDistHigh
//    [0x74] f32  LodDistMed
//    [0x78] f32  LodDistLow
//    [0x7C] f32  LodDistVlow
//    [0x80] u32  RenderMaskFlagsHigh
//    [0x84] u32  RenderMaskFlagsMed
//    [0x88] u32  RenderMaskFlagsLow
//    [0x8C] u32  RenderMaskFlagsVlow
//    [0x90] u64  JointsPointer
//    [0x98] u16  Unknown98
//    [0x9A] u16  DrawableModelsBlocksSize
//    [0x9C] u32  Unknown9C
//    [0xA0] u64  DrawableModelsPointer
//
//  DrawableFile 추가 (@ 0xA8):
//    [0xA8] u64  NamePointer
//    [0xB0] u64  LightAttributesPointer
//    [0xB8] u64  UnkPointer
//    [0xC0] u64  BoundPointer
// =============================================================================

class DrawableFileInfo
{
    public int    FileSize;

    // ResourceFileBase
    public uint   FileVft;
    public ulong  FilePagesInfoPointer;

    // 바운딩 볼륨
    public (float X, float Y, float Z) BoundingCenter;
    public float  BoundingSphereRadius;
    public (float X, float Y, float Z) BoundingBoxMin;
    public (float X, float Y, float Z) BoundingBoxMax;

    // LOD 거리
    public float  LodDistHigh;
    public float  LodDistMed;
    public float  LodDistLow;
    public float  LodDistVlow;

    // 서브 리소스 포인터
    public ulong  ShaderGroupPointer;
    public ulong  SkeletonPointer;
    public ulong  NamePointer;
    public ulong  BoundPointer;

    // 파싱된 서브 오브젝트
    public ShaderGroupInfo?     ShaderGroup;
    public SkeletonInfo?        Skeleton;
    public string               Name = "";

    // LOD 레벨 모델 리스트
    public DrawableModelListInfo? ModelsHigh;
    public DrawableModelListInfo? ModelsMed;
    public DrawableModelListInfo? ModelsLow;
    public DrawableModelListInfo? ModelsVlow;

    public static DrawableFileInfo Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var r = new ResourceReader(data);

        uint magic = r.U32(0);
        if (magic == 0x37435352) // RSC7
            throw new InvalidDataException("RSC7 압축 파일 — 압축 해제된 .ydr 만 지원합니다.");

        var d = new DrawableFileInfo { FileSize = data.Length };

        // ── 단계 1: DrawableBase 헤더 읽기 ────────────────────────────────
        d.FileVft              = r.U32(0x00);
        d.FilePagesInfoPointer = r.U64(0x08);
        d.ShaderGroupPointer   = r.U64(0x10);
        d.SkeletonPointer      = r.U64(0x18);
        d.BoundingCenter       = r.F32x3(0x20);
        d.BoundingSphereRadius = r.F32(0x2C);
        d.BoundingBoxMin       = r.F32x3(0x30);
        d.BoundingBoxMax       = r.F32x3(0x40);

        ulong highPtr  = r.U64(0x50);
        ulong medPtr   = r.U64(0x58);
        ulong lowPtr   = r.U64(0x60);
        ulong vlowPtr  = r.U64(0x68);

        d.LodDistHigh  = r.F32(0x70);
        d.LodDistMed   = r.F32(0x74);
        d.LodDistLow   = r.F32(0x78);
        d.LodDistVlow  = r.F32(0x7C);

        d.NamePointer  = r.U64(0xA8);
        d.BoundPointer = r.U64(0xC0);

        // ── 단계 2: 이름 문자열 읽기 ──────────────────────────────────────
        if (VA.IsSys(d.NamePointer))
            d.Name = r.AsciiStr(VA.FileOffset(d.NamePointer));

        // ── 단계 3: ShaderGroup 읽기 ──────────────────────────────────────
        if (VA.IsSys(d.ShaderGroupPointer))
        {
            int sgOff = VA.FileOffset(d.ShaderGroupPointer);
            if (sgOff + 0x40 <= data.Length)
                d.ShaderGroup = ShaderGroupInfo.Read(r, sgOff);
        }

        // ── 단계 4: Skeleton 읽기 ─────────────────────────────────────────
        if (VA.IsSys(d.SkeletonPointer))
        {
            int skOff = VA.FileOffset(d.SkeletonPointer);
            if (skOff + 0x70 <= data.Length)
                d.Skeleton = SkeletonInfo.Read(r, skOff);
        }

        // ── 단계 5: LOD 레벨별 모델 리스트 읽기 ──────────────────────────
        if (VA.IsSys(highPtr))  d.ModelsHigh  = DrawableModelListInfo.Read(r, VA.FileOffset(highPtr));
        if (VA.IsSys(medPtr))   d.ModelsMed   = DrawableModelListInfo.Read(r, VA.FileOffset(medPtr));
        if (VA.IsSys(lowPtr))   d.ModelsLow   = DrawableModelListInfo.Read(r, VA.FileOffset(lowPtr));
        if (VA.IsSys(vlowPtr))  d.ModelsVlow  = DrawableModelListInfo.Read(r, VA.FileOffset(vlowPtr));

        return d;
    }

    // 전체 지오메트리 수 (High 기준)
    public int TotalGeometries => ModelsHigh?.Models.Sum(m => m.Geometries.Count) ?? 0;

    // 총 삼각형 수 (High 기준, 첫 모델)
    public uint TotalTriangles =>
        (uint)(ModelsHigh?.Models
            .SelectMany(m => m.Geometries)
            .Sum(g => (long)g.TrianglesCount) ?? 0);

    public void Print(string pad, bool verbose)
    {
        // ── 헤더 ────────────────────────────────────────────────────────────
        Out.Log(pad, "DrawableFile", $"file@0x00  (0xC8 bytes)");
        Out.Log(pad, "  [0x00] FileVft",     $"0x{FileVft:X8}");
        Out.Log(pad, "  [0x08] PagesInfoPtr", VA.Describe(FilePagesInfoPointer));
        if (Name.Length > 0)
            Out.Log(pad, "  [0xA8] Name",    $"\"{Name}\"");
        Out.Log(pad, "  [0x2C] SphereRadius", $"{BoundingSphereRadius:F4}");
        Out.Log(pad, "  [0x20] BoundCenter",  Vec3(BoundingCenter));
        if (verbose)
        {
            Out.Log(pad, "  [0x30] BoxMin",  Vec3(BoundingBoxMin));
            Out.Log(pad, "  [0x40] BoxMax",  Vec3(BoundingBoxMax));
        }

        Out.Log(pad, "  LOD 거리",
            $"high={LodDistHigh:F1}  med={LodDistMed:F1}  low={LodDistLow:F1}  vlow={LodDistVlow:F1}");

        // ── ShaderGroup ──────────────────────────────────────────────────────
        if (ShaderGroup != null)
        {
            Console.WriteLine();
            ShaderGroup.Print(pad + "  ", verbose);
        }

        // ── Skeleton ─────────────────────────────────────────────────────────
        if (Skeleton != null)
        {
            Console.WriteLine();
            Skeleton.Print(pad + "  ", verbose);
        }

        // ── LOD 레벨 ─────────────────────────────────────────────────────────
        PrintLod(pad, "High",  ModelsHigh,  verbose);
        PrintLod(pad, "Med",   ModelsMed,   verbose);
        PrintLod(pad, "Low",   ModelsLow,   verbose);
        PrintLod(pad, "Vlow",  ModelsVlow,  verbose);
    }

    static void PrintLod(string pad, string label, DrawableModelListInfo? list, bool verbose)
    {
        if (list == null || list.Models.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine($"{pad}  ── LOD {label}  (models={list.Models.Count})");
        foreach (var m in list.Models)
            m.Print(pad + "    ", verbose);
    }

    public string Summary()
    {
        string name = Name.Length > 0 ? $"\"{Name}\"  " : "";
        string shader = ShaderGroup != null ? $"shaders={ShaderGroup.ShadersCount1}  " : "";
        string skel   = Skeleton    != null ? $"bones={Skeleton.BonesCount}  " : "";
        string high   = ModelsHigh  != null && ModelsHigh.Models.Count > 0
            ? $"high=({ModelsHigh.Models.Count}m/{TotalGeometries}g/{TotalTriangles}t)  "
            : "";
        string med    = ModelsMed   != null && ModelsMed.Models.Count > 0
            ? $"med={ModelsMed.Models.Count}m  " : "";
        string low    = ModelsLow   != null && ModelsLow.Models.Count > 0
            ? $"low={ModelsLow.Models.Count}m  " : "";
        return $"{name}{shader}{skel}{high}{med}{low}r={BoundingSphereRadius:F3}";
    }

    static string Vec3((float X, float Y, float Z) v) =>
        $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}


// =============================================================================
// ── 9. 공통 출력 헬퍼 ────────────────────────────────────────────────────────
// =============================================================================

static class Out
{
    public static void Log(string pad, string field, string value) =>
        Console.WriteLine($"{pad}  {field,-32} {value}");
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
            : Directory.GetFiles(target, "*.ydr", SearchOption.AllDirectories);

        Console.WriteLine($".ydr 파일: {files.Length}개  (verbose={verbose})\n");

        int ok = 0, fail = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"{'═',1}{'═',-60} {Path.GetFileName(path)}");
            try
            {
                var ydr = DrawableFileInfo.Load(path);
                ok++;

                if (verbose || files.Length == 1)
                {
                    Console.WriteLine($"  파일 크기: {ydr.FileSize:N0} bytes");
                    Console.WriteLine();
                    ydr.Print("  ", verbose);
                }
                else
                {
                    Console.WriteLine($"  {ydr.Summary()}");
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

} // namespace YdrReader
