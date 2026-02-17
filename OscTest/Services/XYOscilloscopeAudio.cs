using Avalonia;
using Avalonia.Media;
using DynamicData;
using OpenTK.Audio.OpenAL;
using OpenTK.Compute.OpenCL;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OscTest.Servises;

public class XYOscilloscopeAudio : IDisposable
{
    private ALDevice _device;
    private ALContext _context;


    // 描画ポイント列: [[x, y], [x, y], ...]
    private List<Point> points = [];
    private int index;              // 現在の点インデックス（偶数: p0, 奇数: p1）
    private int subPos = 0;             // 線分内サンプル位置
    private int blankSamples = 0;       // 線分間ブランキングサンプル数

    // 距離ベース制御用
    private List<double> segmentLengths = [];    // 各線分の長さ
    private double totalLength = 0;        // 全線分の合計長

    // 設定
    private int minSamplesPerSegment = 2;
    private double speedScale = 1.0; // 1.0 = 等速、0.5 = 2倍速、2.0 = 半速
    private bool skipNextProcess = false;

    private int _source;
    private int[]? _buffers;

    private int _sampleRate = 48000;

    // Lissajous 用パラメータ（線分が未設定のときのデフォルト表示）
    //public float FrequencyX { get; set; } = 1.0f;
    //public float FrequencyY { get; set; } = 2.0f;
    //private float _phaseX = 0f;
    //private float _phaseY = 0f;

    // 線分／折れ線用の現在のポイント列（-1〜+1 の正規化座標）
    //private List<Point> _currentLine = new List<Point>();
    //private int _lineIndex = 0;

    public void Init()
    {
        _device = ALC.OpenDevice(null);
        ALContextAttributes a = new ALContextAttributes();
        if (a.Frequency.HasValue)
            _sampleRate = a.Frequency.Value;

        _context = ALC.CreateContext(_device, a);
        ALC.MakeContextCurrent(_context);

        _source = AL.GenSource();
        _buffers = AL.GenBuffers(4);

        //// 初期バッファ投入
        for (int i = 0; i < _buffers.Length; i++)
        {
            var frameSamples = (_sampleRate / 60);
            var buffer = new float[frameSamples * 2];
            AL.BufferData(_buffers[i], ALFormat.StereoFloat32Ext, buffer, _sampleRate);
            AL.SourceQueueBuffer(_source, _buffers[i]);
        }

        AL.SourcePlay(_source);
    }

    public void SetupSegments()
    {
        segmentLengths = new List<double>();
        totalLength = 0;

        int N = points.Count;
        if (N < 2) return;

        for (int i = 0; i < N; i += 2)
        {
            var p0 = points[i];
            var p1 = points[(i + 1) % N];

            // ★ 線分クリップ
            var clipped = ClipSegment(p0, p1);
            if (clipped != null)
            {
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);

                segmentLengths.Add(len);
                totalLength += len;
            }
        }

        if (totalLength <= 0)
        {
            totalLength = 1e-6;
        }
    }

    // 線分と境界の交点を求める
    private static Point? Intersect(
        Point p0,
        Point p1,
        double bound,
        bool isX)
    {
        var (x0, y0) = p0;
        var (x1, y1) = p1;

        if (isX)
        {
            double dx = x1 - x0;
            if (dx == 0) return null;

            double t = (bound - x0) / dx;
            if (t < 0 || t > 1) return null;

            return new Point(bound, y0 + (y1 - y0) * t);
        }
        else
        {
            double dy = y1 - y0;
            if (dy == 0) return null;

            double t = (bound - y0) / dy;
            if (t < 0 || t > 1) return null;

            return new Point(x0 + (x1 - x0) * t, bound);
        }
    }

    public Point[]? ClipSegment(
        Point p0,
        Point p1)
    {
        return [p0, p1];

        bool Inside(Point p)
            => p.X >= -1 && p.X <= 1 && p.Y >= -1 && p.Y <= 1;

        bool p0Inside = Inside(p0);
        bool p1Inside = Inside(p1);

        // 境界 (bound, isX)
        var bounds = new (double bound, bool isX)[]
        {
        (-1, true),   // x = -1
        ( 1, true),   // x =  1
        (-1, false),  // y = -1
        ( 1, false),  // y =  1
        };

        var intersections = new List<Point>();

        foreach (var (bound, isX) in bounds)
        {
            var p = Intersect(p0, p1, bound, isX);
            if (p != null)
                intersections.Add(p.Value);
        }

        // ★ ケース1：両端が内側
        if (p0Inside && p1Inside)
        {
            return [p0, p1];
        }

        // ★ ケース2：両端が外側
        if (!p0Inside && !p1Inside)
        {
            if (intersections.Count == 2)
            {
                // p0→p1 の順序で並べる
                var sorted = intersections
                    .OrderBy(p => (p.X - p0.X) * (p.X - p0.X)
                                + (p.Y - p0.Y) * (p.Y - p0.Y))
                    .ToArray();

                return [sorted[0], sorted[1]];
            }
            return null;
        }

        // ★ ケース3：p0 外 → p1 内
        if (!p0Inside && p1Inside)
        {
            if (intersections.Count > 0)
            {
                var i0 = intersections
                    .OrderBy(p => (p.X - p0.X) * (p.X - p0.X)
                                + (p.Y - p0.Y) * (p.Y - p0.Y))
                    .First();

                return [i0, p1];
            }
            return null;
        }

        // ★ ケース4：p0 内 → p1 外
        if (p0Inside && !p1Inside)
        {
            if (intersections.Count > 0)
            {
                var i0 = intersections
                    .OrderBy(p => (p.X - p1.X) * (p.X - p1.X)
                                + (p.Y - p1.Y) * (p.Y - p1.Y))
                    .First();

                return [p0, i0];
            }
            return null;
        }

        return null;
    }


    public void Update()
    {
        var N = points.Count;
        var frameSamples = (_sampleRate / 60);
        var buffer = new float[frameSamples * 2];
        // 全長をこのフレームのサンプル数に収めるスケール
        var scale = (frameSamples / totalLength) * speedScale;

        //ストリーミング再生では、AL_BUFFERS_PROCESSED を使って「再生が終わったバッファ数」を取得します。
        AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);

        if (N < 2)
        {
            this.skipNextProcess = false;
            return;
        }

        while (processed-- > 0)
        {
            int buf = AL.SourceUnqueueBuffer(_source);

            //float[] pcm = GenerateXYBuffer();
            float[] pcm = new float[frameSamples * 2];

            for (int i = 0; i < frameSamples; i++)
            {
                var p0 = points[index];
                var p1 = points[(index + 1) % N];

                var clipped = ClipSegment(p0, p1);

                // ★ クリップで完全に消えた
                if (clipped == null)
                {
                    this.subPos = 0;

                    if (this.index + 2 >= N)
                        this.skipNextProcess = false;
                    this.index = (this.index + 2) % N;
                    pcm[i * 2 + 0] = 0;
                    pcm[i * 2 + 1] = 0;
                    continue;
                }

                var dx = clipped[1].X - clipped[0].X;
                var dy = clipped[1].Y - clipped[0].Y;
                var len = Math.Sqrt(dx * dx + dy * dy);

                // ★ クリップ後の長さが 0 → 次へ
                if (len <= 1e-9)
                {
                    this.subPos = 0;
                    this.index = (this.index + 2) % N;
                    pcm[i * 2 + 0] = 0;
                    pcm[i * 2 + 1] = 0;
                    continue;
                }

                // ★ クリップ後の長さでサンプル数を決める
                var samplesPerSegment = Math.Max(
                    this.minSamplesPerSegment,
                    Math.Floor(len * scale)
                );

                var t = this.subPos / samplesPerSegment;

                var x = clipped[0].X + dx * t;
                var y = clipped[0].Y + dy * t;

                pcm[i * 2 + 0] = (float)x;
                pcm[i * 2 + 1] = (float)y;

                this.subPos++;

                // ★ 線分終了 → 次へ
                if (this.subPos >= samplesPerSegment + this.blankSamples)
                {
                    this.subPos = 0;
                    if (this.index + 2 >= N)
                        this.skipNextProcess = false;
                    this.index = (this.index + 2) % N;
                }
            }

            AL.BufferData(buf, ALFormat.StereoFloat32Ext, pcm.ToArray(), _sampleRate);
            AL.SourceQueueBuffer(_source, buf);
        }

        skipNextProcess = false;
    }

    /// <summary>
    /// 線分（または折れ線の 1 セグメント）を設定する。
    /// 座標は -1〜+1 の範囲で指定することを想定。
    /// </summary>
    public void SetLine(Point p0, Point p1, int samples = 2000)
    {
        if (this.skipNextProcess)
            return;

        this.skipNextProcess = true;

        points.Add(p0);
        points.Add(p1);

        this.index = 0;
        this.subPos = 0;
        this.SetupSegments();

        //_currentLine = GenerateLinePoints(p0, p1, samples);
        //_lineIndex = 0;
    }

    /// <summary>
    /// 複数線分（折れ線）をまとめて設定する。
    /// 各点は -1〜+1 の範囲で指定。
    /// </summary>
    public void SetPolyline(List<Point> points, int samplesPerSegment = 500)
    {
        if (this.skipNextProcess)
            return;

        this.skipNextProcess = true;

        this.points = points;

        this.index = 0;
        this.subPos = 0;
        this.SetupSegments();

        //if (points == null || points.Count < 2)
        //{
        //    this.points.Clear();
        //    _currentLine.Clear();
        //    _lineIndex = 0;
        //    return;
        //}

        //var list = new List<Point>();

        //for (int i = 0; i < points.Count - 1; i++)
        //{
        //    var p0 = points[i];
        //    var p1 = points[i + 1];
        //    list.AddRange(GenerateLinePoints(p0, p1, samplesPerSegment));
        //}

        //_currentLine = list;
        //_lineIndex = 0;
    }

    private List<Point> GenerateLinePoints(
        Point p0, Point p1, int samples)
    {
        var list = new List<Point>(samples);

        for (int i = 0; i < samples; i++)
        {
            var t = i / (samples - 1);
            var x = p0.X + (p1.X - p0.X) * t;
            var y = p0.Y + (p1.Y - p0.Y) * t;
            list.Add(new Point(x, y));
        }

        return list;
    }

    public void Dispose()
    {
        try
        {
            if (_source != 0)
            {
                AL.SourceStop(_source);
                AL.DeleteSource(_source);
                _source = 0;
            }

            if (_buffers != null)
            {
                AL.DeleteBuffers(_buffers);
                _buffers = null;
            }

            if (_context != ALContext.Null)
            {
                ALC.MakeContextCurrent(ALContext.Null);
                ALC.DestroyContext(_context);
                _context = ALContext.Null;
            }

            if (_device != ALDevice.Null)
            {
                ALC.CloseDevice(_device);
                _device = ALDevice.Null;
            }
        }
        catch
        {
            // 破棄時の例外は握りつぶす
        }
    }
}