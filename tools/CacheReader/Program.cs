// =============================================================================
// CacheReader — GTA5 gta5_cache_y.dat 파일 파서 (학습용, 독립 실행)
//
// .ybn/.ymap/.ytyp 과는 달리 이 파일은 "혼합 포맷"입니다:
//   - 앞부분: 텍스트 (버전 문자열, XML 스타일 태그, fileDates)
//   - 중간: 텍스트 태그 사이에 바이너리 블록이 삽입됨
//
// 파일 구조:
// ┌─ [0x00..0x63] 100 bytes ─────────────────────────────────────────────────┐
// │  "[VERSION]\nXXXX\n"  — 버전 문자열, 남은 공간은 \0 패딩                │
// └──────────────────────────────────────────────────────────────────────────┘
// ┌─ [0x64..] 텍스트/바이너리 혼합 ────────────────────────────────────────────┐
// │  <fileDates>\n                                                            │
// │  "해시 타임스탬프 [파일ID]\n"  (반복)                                    │
// │  </fileDates>\n                                                           │
// │                                                                           │
// │  <module>\n                                                               │
// │  fwMapDataStore\n                                                         │
// │  [u32 blockLen][MapDataStoreNode × N (각 64 bytes)]  ← 순수 바이너리     │
// │  </module>\n                                                              │
// │                                                                           │
// │  <module>\n                                                               │
// │  CInteriorProxy\n                                                         │
// │  [u32 blockLen][CInteriorProxy × N (각 104 bytes)]                        │
// │  </module>\n                                                              │
// │                                                                           │
// │  <module>\n                                                               │
// │  BoundsStore\n                                                            │
// │  [u32 blockLen][BoundsStoreItem × N (각 32 bytes)]                        │
// │  </module>\n                                                              │
// └──────────────────────────────────────────────────────────────────────────┘
//
// 실행:  dotnet run                             (기본 경로 사용)
//        dotnet run -- "경로/gta5_cache_y.dat"
//        dotnet run -- "경로/gta5_cache_y.dat" --verbose
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CacheReader
{

// =============================================================================
// ── 1. BinaryReader 래퍼 (리틀 엔디언, position 추적)
// =============================================================================

class ByteReader
{
    readonly byte[] _data;
    int _pos;

    public ByteReader(byte[] data, int startPos = 0)
    {
        _data = data;
        _pos  = startPos;
    }

    public int  Position { get => _pos; set => _pos = value; }
    public int  Length   => _data.Length;

    public byte   U8()  => _data[_pos++];
    public ushort U16() { var v = BitConverter.ToUInt16(_data, _pos); _pos += 2; return v; }
    public uint   U32() { var v = BitConverter.ToUInt32(_data, _pos); _pos += 4; return v; }
    public ulong  U64() { var v = BitConverter.ToUInt64(_data, _pos); _pos += 8; return v; }
    public float  F32() { var v = BitConverter.ToSingle (_data, _pos); _pos += 4; return v; }

    public (float X, float Y, float Z) F32x3() => (F32(), F32(), F32());
    public (float X, float Y, float Z, float W) F32x4() => (F32(), F32(), F32(), F32());
}


// =============================================================================
// ── 2. CacheFileDate — fileDates 섹션의 한 줄
//
//  형식: "해시_u32 타임스탬프_i64 [파일ID_u32]\n"
//  예시: "2740459947 130680580712018938 8944"
//
//  해시: 리소스 파일 경로의 JenkHash
//         (예: "platform:/data/cdimages/scaleform_frontend.rpf")
//  타임스탬프: Windows FILETIME (100-나노초 단위, 1601-01-01 기준)
//  파일ID: RPF 내부 파일 식별자 (없는 경우도 있음)
// =============================================================================

class CacheFileDate
{
    public uint  FileHash;    // JenkHash of resource path
    public long  TimeStamp;   // Windows FILETIME
    public uint  FileId;      // 0 = 없음

    public static CacheFileDate? ParseLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 2) return null;
        if (!uint.TryParse(parts[0], out uint hash)) return null;
        if (!long.TryParse(parts[1], out long ts)) return null;
        uint fid = parts.Length > 2 && uint.TryParse(parts[2], out uint v) ? v : 0;
        return new CacheFileDate { FileHash = hash, TimeStamp = ts, FileId = fid };
    }

    // Windows FILETIME → DateTime (UTC)
    public DateTime TimeStampUtc => DateTime.FromFileTimeUtc(TimeStamp);

    public string Summary() =>
        FileId == 0
            ? $"0x{FileHash:X8}  {TimeStampUtc:yyyy-MM-dd HH:mm:ss}"
            : $"0x{FileHash:X8}  {TimeStampUtc:yyyy-MM-dd HH:mm:ss}  fileId={FileId}";
}


// =============================================================================
// ── 3. MapDataStoreNode — fwMapDataStore 바이너리 레코드 (64 bytes)
//
//  .ymap 의 CMapData 에서 뽑은 공간 인덱스 데이터입니다.
//  게임이 시작할 때 이 캐시를 읽어 어떤 .ymap 을 로드할지 결정합니다.
//
//  Binary layout (64 bytes, 리틀 엔디언):
//   [ 0] u32  name              — .ymap 이름 해시 (CMapData.name)
//   [ 4] u32  parentName        — 부모 .ymap 이름 해시 (0 = 루트)
//   [ 8] u32  contentFlags      — 콘텐츠 종류 플래그 (CMapData.contentFlags)
//   [12] f32x3 streamingExtentsMin  — 스트리밍 범위 최솟값
//   [24] f32x3 streamingExtentsMax  — 스트리밍 범위 최댓값
//   [36] f32x3 entitiesExtentsMin   — 엔티티 범위 최솟값
//   [48] f32x3 entitiesExtentsMax   — 엔티티 범위 최댓값
//   [60] u8   unk1  — HD 플래그 (critical/long/strm 구분 추정)
//   [61] u8   unk2  — LOD 플래그 (기본 맵 파일 추정)
//   [62] u8   unk3  — SLOD 플래그 추정
//   [63] u8   unk4  — 예약됨
// =============================================================================

class MapDataStoreNode
{
    public uint  Name;
    public uint  ParentName;
    public uint  ContentFlags;
    public (float X, float Y, float Z) StreamingExtentsMin;
    public (float X, float Y, float Z) StreamingExtentsMax;
    public (float X, float Y, float Z) EntitiesExtentsMin;
    public (float X, float Y, float Z) EntitiesExtentsMax;
    public byte  Unk1, Unk2, Unk3, Unk4;

    public const int SIZE = 64;

    public static MapDataStoreNode Read(ByteReader r)
    {
        return new MapDataStoreNode
        {
            Name                 = r.U32(),
            ParentName           = r.U32(),
            ContentFlags         = r.U32(),
            StreamingExtentsMin  = r.F32x3(),
            StreamingExtentsMax  = r.F32x3(),
            EntitiesExtentsMin   = r.F32x3(),
            EntitiesExtentsMax   = r.F32x3(),
            Unk1 = r.U8(), Unk2 = r.U8(), Unk3 = r.U8(), Unk4 = r.U8(),
        };
    }

    public bool IsRoot => ParentName == 0;

    public string Summary() =>
        $"0x{Name:X8}  parent=0x{ParentName:X8}  flags=0x{ContentFlags:X8}  " +
        $"flags=[{FlagDesc()}]";

    string FlagDesc()
    {
        // ContentFlags 비트 의미 (CodeWalker Space.cs 기준 추정)
        var f = new List<string>();
        if ((ContentFlags & 1) != 0)  f.Add("Hd");
        if ((ContentFlags & 2) != 0)  f.Add("Lod");
        if ((ContentFlags & 4) != 0)  f.Add("SLod1");
        if ((ContentFlags & 8) != 0)  f.Add("SLod2");
        if ((ContentFlags & 16) != 0) f.Add("SLod3");
        if ((ContentFlags & 32) != 0) f.Add("Physics");
        if ((ContentFlags & 64) != 0) f.Add("NavMesh");
        if ((ContentFlags & 256)!= 0) f.Add("Interior");
        return f.Count > 0 ? string.Join("|", f) : $"0x{ContentFlags:X8}";
    }
}


// =============================================================================
// ── 4. CInteriorProxy — CInteriorProxy 바이너리 레코드 (104 bytes)
//
//  인테리어 프록시 캐시 데이터입니다.
//  어느 .ymap 에 어떤 인테리어가 속하는지, 그 위치/방향/경계를 기록합니다.
//
//  Binary layout (104 bytes, 리틀 엔디언):
//   [ 0] u32  unk01     — 인테리어 타입 ID (v_cashdepot=0, dt1_02_carpark=19, ...)
//   [ 4] u32  unk02     — 항상 0 (예약됨)
//   [ 8] u32  unk03     — 인테리어 종류 (v_fib01=8, v_cashdepot=6, v_gun=1, ...)
//   [12] u32  name      — 인테리어 이름 해시 (MLO archetype name)
//   [16] u32  parent    — 소속 .ymap 이름 해시 (MapDataStoreNode.name 과 대응)
//   [20] f32x3 position — 월드 좌표
//   [32] f32x4 orientation — 쿼터니언 (X Y Z W)
//   [48] f32x3 bbMin    — 바운딩 박스 최솟값
//   [60] f32x3 bbMax    — 바운딩 박스 최댓값
//   [72] u64  unk11     — 파일 오프셋 추정
//   [80] u64  unk12     — 파일 오프셋 추정
//   [88] u64  unk13     — 파일 오프셋 추정
//   [96] u64  unk14     — 파일 오프셋 추정 (unk14 - unk13 ≈ 5,500,000)
// =============================================================================

class CInteriorProxy
{
    public uint  Unk01;
    public uint  Unk02;
    public uint  Unk03;
    public uint  Name;
    public uint  Parent;
    public (float X, float Y, float Z) Position;
    public (float X, float Y, float Z, float W) Orientation;
    public (float X, float Y, float Z) BbMin;
    public (float X, float Y, float Z) BbMax;
    public ulong Unk11, Unk12, Unk13, Unk14;

    public const int SIZE = 104;

    public static CInteriorProxy Read(ByteReader r)
    {
        return new CInteriorProxy
        {
            Unk01       = r.U32(),
            Unk02       = r.U32(),
            Unk03       = r.U32(),
            Name        = r.U32(),
            Parent      = r.U32(),
            Position    = r.F32x3(),
            Orientation = r.F32x4(),
            BbMin       = r.F32x3(),
            BbMax       = r.F32x3(),
            Unk11       = r.U64(),
            Unk12       = r.U64(),
            Unk13       = r.U64(),
            Unk14       = r.U64(),
        };
    }

    public string Summary() =>
        $"name=0x{Name:X8}  parent=0x{Parent:X8}  " +
        $"pos=({Position.X:F1},{Position.Y:F1},{Position.Z:F1})  " +
        $"type={Unk01}  kind={Unk03}";
}


// =============================================================================
// ── 5. BoundsStoreItem — BoundsStore 바이너리 레코드 (32 bytes)
//
//  물리 충돌(.ybn) 파일의 바운딩 박스 캐시입니다.
//  어느 .ybn 이 어느 공간을 차지하는지 기록하여 스트리밍 시 빠른 범위 검색을 지원합니다.
//
//  Binary layout (32 bytes, 리틀 엔디언):
//   [ 0] u32   name   — .ybn 파일명 해시 (archetypeName 해시)
//   [ 4] f32x3 min    — 바운딩 박스 최솟값
//   [16] f32x3 max    — 바운딩 박스 최댓값
//   [28] u32   layer  — 레이어 ID (물리 레이어 분류)
// =============================================================================

class BoundsStoreItem
{
    public uint  Name;
    public (float X, float Y, float Z) Min;
    public (float X, float Y, float Z) Max;
    public uint  Layer;

    public const int SIZE = 32;

    public static BoundsStoreItem Read(ByteReader r)
    {
        return new BoundsStoreItem
        {
            Name  = r.U32(),
            Min   = r.F32x3(),
            Max   = r.F32x3(),
            Layer = r.U32(),
        };
    }

    public string Summary() =>
        $"0x{Name:X8}  min=({Min.X:F1},{Min.Y:F1},{Min.Z:F1})  " +
        $"max=({Max.X:F1},{Max.Y:F1},{Max.Z:F1})  layer={Layer}";
}


// =============================================================================
// ── 6. CacheDatFile — 전체 파일 파서
//
//  파싱 알고리즘:
//    ① [0x00..0x63] 버전 문자열 읽기 (첫 \0 까지)
//
//    ② [0x64..] 바이트를 한 줄씩 읽기 ('\n' = 0x0A 구분)
//       ┌ 줄 내용에 따라 분기:
//       │  "<fileDates>"    → 날짜 수집 모드 ON
//       │  "</fileDates>"   → 날짜 수집 모드 OFF
//       │  "fwMapDataStore" → ★ 바이너리 블록 읽기 (MapDataStoreNode)
//       │  "CInteriorProxy" → ★ 바이너리 블록 읽기 (CInteriorProxy)
//       │  "BoundsStore"    → ★ 바이너리 블록 읽기 (BoundsStoreItem)
//       │  날짜 수집 모드 중 → CacheFileDate 파싱
//       └ 그 외 ("", <module>, </module>) → 무시
//
//    ★ 바이너리 블록 읽기:
//       현재 위치 + 1 (줄바꿈 다음)에서 u32 blockLen 읽기
//       blockLen / 레코드크기 = 레코드 수
//       레코드를 순차적으로 읽고, i를 (blockLen+4) 만큼 건너뜀
// =============================================================================

class CacheDatFile
{
    public string              Version      = "";
    public CacheFileDate[]     FileDates    = [];
    public MapDataStoreNode[]  MapNodes     = [];
    public CInteriorProxy[]    InteriorProxies = [];
    public BoundsStoreItem[]   BoundsItems  = [];

    public static CacheDatFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var file = new CacheDatFile();

        // ── ① 버전 문자열 읽기 (최대 100 bytes) ──────────────────────────
        var vsb = new StringBuilder();
        for (int i = 0; i < 100 && i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0) break;
            vsb.Append((char)b);
        }
        file.Version = vsb.ToString()
            .Replace("[VERSION]", "").Replace("\r", "").Replace("\n", "").Trim();

        // ── ② 텍스트/바이너리 혼합 영역 파싱 ────────────────────────────
        var dates        = new List<CacheFileDate>();
        var mapNodes     = new List<MapDataStoreNode>();
        var intProxies   = new List<CInteriorProxy>();
        var boundsItems  = new List<BoundsStoreItem>();

        bool inDates = false;
        var  lineBuf = new StringBuilder();

        for (int i = 100; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0) break;           // 파일 끝
            if (b != 0x0A)               // 줄바꿈이 아니면 버퍼에 추가
            {
                lineBuf.Append((char)b);
                continue;
            }

            // ── 줄바꿈 도달 → 한 줄 처리 ─────────────────────────────────
            string line = lineBuf.ToString();
            lineBuf.Clear();

            switch (line)
            {
                case "<fileDates>":
                    inDates = true;
                    break;

                case "</fileDates>":
                    inDates = false;
                    break;

                case "<module>":
                case "</module>":
                    break;

                // ── fwMapDataStore 바이너리 블록 ─────────────────────────
                case "fwMapDataStore":
                {
                    // i+1 위치에 u32 blockLen, 이후 MapDataStoreNode 배열
                    int blockStart = i + 1;
                    uint blockLen  = BitConverter.ToUInt32(data, blockStart);
                    int  endPos    = blockStart + 4 + (int)blockLen;
                    var  r         = new ByteReader(data, blockStart + 4);
                    while (r.Position < endPos)
                        mapNodes.Add(MapDataStoreNode.Read(r));
                    i += (int)(blockLen + 4);  // 바이너리 블록 건너뜀
                    break;
                }

                // ── CInteriorProxy 바이너리 블록 ─────────────────────────
                case "CInteriorProxy":
                {
                    int blockStart = i + 1;
                    uint blockLen  = BitConverter.ToUInt32(data, blockStart);
                    int  endPos    = blockStart + 4 + (int)blockLen;
                    var  r         = new ByteReader(data, blockStart + 4);
                    while (r.Position < endPos)
                        intProxies.Add(CInteriorProxy.Read(r));
                    i += (int)(blockLen + 4);
                    break;
                }

                // ── BoundsStore 바이너리 블록 ────────────────────────────
                case "BoundsStore":
                {
                    int blockStart = i + 1;
                    uint blockLen  = BitConverter.ToUInt32(data, blockStart);
                    int  endPos    = blockStart + 4 + (int)blockLen;
                    var  r         = new ByteReader(data, blockStart + 4);
                    while (r.Position < endPos)
                        boundsItems.Add(BoundsStoreItem.Read(r));
                    i += (int)(blockLen + 4);
                    break;
                }

                default:
                    if (inDates)
                    {
                        var d = CacheFileDate.ParseLine(line);
                        if (d != null) dates.Add(d);
                    }
                    break;
            }
        }

        file.FileDates       = dates.ToArray();
        file.MapNodes        = mapNodes.ToArray();
        file.InteriorProxies = intProxies.ToArray();
        file.BoundsItems     = boundsItems.ToArray();

        return file;
    }
}


// =============================================================================
// ── 7. 출력 헬퍼
// =============================================================================

static class Out
{
    public static void Log(string prefix, string field, string value) =>
        Console.WriteLine($"{prefix}  {field,-28} {value}");
}


// =============================================================================
// ── 8. 진입점
// =============================================================================

class Program
{
    static void Main(string[] args)
    {
        string target  = @"E:\myGames-Resources\GTA5-DAT\data\gta5_cache_y.dat";
        bool   verbose = false;

        foreach (var a in args)
        {
            if (a == "--verbose" || a == "-v") verbose = true;
            else target = a;
        }

        // 파일 또는 디렉토리 모두 지원
        string[] files = Directory.Exists(target)
            ? Directory.GetFiles(target, "gta5_cache_y.dat", SearchOption.AllDirectories)
            : File.Exists(target)
            ? [target]
            : [];

        if (files.Length == 0)
        {
            Console.WriteLine($"파일을 찾을 수 없습니다: {target}");
            return;
        }

        foreach (var path in files)
        {
            Console.WriteLine($"══ {Path.GetFileName(path)}  ({Path.GetDirectoryName(path)})");
            Console.WriteLine($"   파일 크기: {new FileInfo(path).Length:N0} bytes");
            Console.WriteLine();

            try
            {
                var cache = CacheDatFile.Load(path);

                // ── 기본 정보 ─────────────────────────────────────────────
                Out.Log("", "Version",         cache.Version);
                Out.Log("", "FileDates",       $"{cache.FileDates.Length}개");
                Out.Log("", "MapNodes",        $"{cache.MapNodes.Length}개  (fwMapDataStore, 각 64 bytes)");
                Out.Log("", "InteriorProxies", $"{cache.InteriorProxies.Length}개  (CInteriorProxy, 각 104 bytes)");
                Out.Log("", "BoundsItems",     $"{cache.BoundsItems.Length}개  (BoundsStore, 각 32 bytes)");
                Console.WriteLine();

                // ── fileDates ─────────────────────────────────────────────
                if (cache.FileDates.Length > 0)
                {
                    Console.WriteLine("  [fileDates]");
                    int show = verbose
                        ? cache.FileDates.Length
                        : Math.Min(cache.FileDates.Length, 8);
                    for (int i = 0; i < show; i++)
                        Console.WriteLine($"    [{i:D3}] {cache.FileDates[i].Summary()}");
                    if (show < cache.FileDates.Length)
                        Console.WriteLine($"    ... 이하 {cache.FileDates.Length - show}개 생략");
                    Console.WriteLine();
                }

                // ── MapDataStoreNode ──────────────────────────────────────
                if (cache.MapNodes.Length > 0)
                {
                    int rootCount = 0;
                    foreach (var n in cache.MapNodes)
                        if (n.IsRoot) rootCount++;

                    Console.WriteLine($"  [fwMapDataStore]  (루트={rootCount}개, 자식={cache.MapNodes.Length - rootCount}개)");
                    int show = verbose
                        ? cache.MapNodes.Length
                        : Math.Min(cache.MapNodes.Length, 10);
                    for (int i = 0; i < show; i++)
                    {
                        var n = cache.MapNodes[i];
                        string rootMark = n.IsRoot ? " ←루트" : "";
                        Console.WriteLine($"    [{i:D4}] {n.Summary()}{rootMark}");
                        if (verbose)
                        {
                            Out.Log("      ", "streamExtMin",
                                $"({n.StreamingExtentsMin.X:F1},{n.StreamingExtentsMin.Y:F1},{n.StreamingExtentsMin.Z:F1})");
                            Out.Log("      ", "streamExtMax",
                                $"({n.StreamingExtentsMax.X:F1},{n.StreamingExtentsMax.Y:F1},{n.StreamingExtentsMax.Z:F1})");
                            Out.Log("      ", "entityExtMin",
                                $"({n.EntitiesExtentsMin.X:F1},{n.EntitiesExtentsMin.Y:F1},{n.EntitiesExtentsMin.Z:F1})");
                            Out.Log("      ", "entityExtMax",
                                $"({n.EntitiesExtentsMax.X:F1},{n.EntitiesExtentsMax.Y:F1},{n.EntitiesExtentsMax.Z:F1})");
                            Out.Log("      ", "unk1/2/3/4",
                                $"{n.Unk1} / {n.Unk2} / {n.Unk3} / {n.Unk4}");
                        }
                    }
                    if (show < cache.MapNodes.Length)
                        Console.WriteLine($"    ... 이하 {cache.MapNodes.Length - show}개 생략");
                    Console.WriteLine();
                }

                // ── CInteriorProxy ────────────────────────────────────────
                if (cache.InteriorProxies.Length > 0)
                {
                    Console.WriteLine($"  [CInteriorProxy]  ({cache.InteriorProxies.Length}개)");
                    int show = verbose
                        ? cache.InteriorProxies.Length
                        : Math.Min(cache.InteriorProxies.Length, 10);
                    for (int i = 0; i < show; i++)
                    {
                        var p = cache.InteriorProxies[i];
                        Console.WriteLine($"    [{i:D3}] {p.Summary()}");
                        if (verbose)
                        {
                            Out.Log("      ", "orientation",
                                $"({p.Orientation.X:F3},{p.Orientation.Y:F3}," +
                                $"{p.Orientation.Z:F3},{p.Orientation.W:F3})");
                            Out.Log("      ", "bbMin",
                                $"({p.BbMin.X:F1},{p.BbMin.Y:F1},{p.BbMin.Z:F1})");
                            Out.Log("      ", "bbMax",
                                $"({p.BbMax.X:F1},{p.BbMax.Y:F1},{p.BbMax.Z:F1})");
                            Out.Log("      ", "unk11~14",
                                $"{p.Unk11} / {p.Unk12} / {p.Unk13} / {p.Unk14}");
                        }
                    }
                    if (show < cache.InteriorProxies.Length)
                        Console.WriteLine($"    ... 이하 {cache.InteriorProxies.Length - show}개 생략");
                    Console.WriteLine();
                }

                // ── BoundsStore ───────────────────────────────────────────
                if (cache.BoundsItems.Length > 0)
                {
                    Console.WriteLine($"  [BoundsStore]  ({cache.BoundsItems.Length}개)");
                    int show = verbose
                        ? cache.BoundsItems.Length
                        : Math.Min(cache.BoundsItems.Length, 10);
                    for (int i = 0; i < show; i++)
                        Console.WriteLine($"    [{i:D4}] {cache.BoundsItems[i].Summary()}");
                    if (show < cache.BoundsItems.Length)
                        Console.WriteLine($"    ... 이하 {cache.BoundsItems.Length - show}개 생략");
                    Console.WriteLine();
                }

                Console.Write( "계속하려면 아무키나 누르세요 : " );
                Console.Read();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [실패] {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}

} // namespace CacheReader
