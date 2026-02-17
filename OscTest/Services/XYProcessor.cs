using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using OpenTK.Audio.OpenAL;

namespace OscTest.Services
{
    public class XYProcessor
    {
        // OpenAL
        private readonly int _source;
        private readonly int _sampleRate;

        // JS版の状態
        private List<Point> _points = new();
        private int _index = 0;              // 現在の点インデックス（偶数: p0, 奇数: p1）
        private int _subPos = 0;             // 線分内サンプル位置
        private int _blankSamples = 0;       // 線分間ブランキングサンプル数

        // 距離ベース制御用
        private List<double> _segmentLengths = new(); // 各線分の長さ
        private double _totalLength = 0;             // 全線分の合計長

        // 設定
        private int _minSamplesPerSegment = 2;
        private double _speedScale = 1.0;   // 1.0 = 等速、0.5 = 2倍速、2.0 = 半速
        private bool _skipNextProcess = false;

        // 1フレームあたりのサンプル数（AudioWorklet の outL.length 相当）
        private readonly int _frameSamples;

        /// <summary>
        /// Initializes a new instance of the XYProcessor class with the specified data source, sample rate, and frame
        /// sample size.
        /// </summary>
        /// <remarks>The XYProcessor is intended for audio or signal processing scenarios. The
        /// frameSamples parameter allows for flexibility in processing different frame sizes, which can affect
        /// performance and analysis granularity.</remarks>
        /// <param name="source">The identifier for the data source to be processed. Must be a valid source identifier.</param>
        /// <param name="sampleRate">The sample rate, in hertz (Hz), at which the data will be processed. Must be greater than zero.</param>
        /// <param name="frameSamples">The number of samples per frame. Must be a positive integer. The default value is 1024.</param>
        public XYProcessor(int source, int sampleRate, int frameSamples = 1024)
        {
            _source = source;
            _sampleRate = sampleRate;
            _frameSamples = frameSamples;
        }

        // JS: setPoints + _setupSegments
        /// <summary>
        /// Sets the collection of points to be processed by the instance.
        /// </summary>
        /// <remarks>This method has no effect if the previous operation was marked to be skipped. After
        /// setting the points, the internal state is initialized for segment processing.</remarks>
        /// <param name="points">The list of points to set. If null, an empty list is used instead.</param>
        public void SetPoints(List<Point> points)
        {
            if (_skipNextProcess)
                return;

            _skipNextProcess = true;
            _points = points ?? new List<Point>();
            _index = 0;
            _subPos = 0;
            SetupSegments();
        }

        /// <summary>
        /// Initializes the segment lengths and calculates the total length of all valid segments formed by pairs of
        /// points in the collection.
        /// </summary>
        /// <remarks>This method clears any existing segment length data and processes the points in the
        /// collection as pairs, treating the collection as circular by connecting the last point back to the first. If
        /// fewer than two points are present, no segments are created. If no valid segments are found, a minimal total
        /// length is assigned to prevent division by zero in subsequent calculations.</remarks>
        private void SetupSegments()
        {
            _segmentLengths.Clear();
            _totalLength = 0f;

            int N = _points.Count;
            if (N < 2) return;

            for (int i = 0; i < N; i += 2)
            {
                var p0 = _points[i];
                var p1 = _points[(i + 1) % N];

                var clipped = ClipSegment(p0, p1);
                if (clipped != null)
                {
                    var (a, b) = clipped.Value;
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);

                    _segmentLengths.Add(len);
                    _totalLength += len;
                }
            }

            if (_totalLength <= 0f)
                _totalLength = 1e-6f;

            //HACK: 通常はコメントアウトする
            _totalLength = 10;
        }

        // JS: intersect(p0, p1, bound, isX)
        /// <summary>
        /// Calculates the intersection point between a line segment defined by two points and a specified axis-aligned
        /// boundary.
        /// </summary>
        /// <remarks>The method returns <see langword="null"/> if the line segment is parallel to the
        /// specified boundary or if the intersection point lies outside the segment.</remarks>
        /// <param name="p0">The starting point of the line segment.</param>
        /// <param name="p1">The ending point of the line segment.</param>
        /// <param name="bound">The coordinate value of the boundary to intersect with. Interpreted as an x-coordinate if <paramref
        /// name="isX"/> is <see langword="true"/>, or as a y-coordinate if <paramref name="isX"/> is <see
        /// langword="false"/>.</param>
        /// <param name="isX">A value indicating whether the intersection is calculated with respect to the x-axis (<see
        /// langword="true"/>) or the y-axis (<see langword="false"/>).</param>
        /// <returns>A <see cref="Point"/> representing the intersection point if the line segment crosses the specified
        /// boundary; otherwise, <see langword="null"/> if there is no intersection within the segment.</returns>
        private Point? Intersect(Point p0, Point p1, double bound, bool isX)
        {
            double x0 = p0.X, y0 = p0.Y;
            double x1 = p1.X, y1 = p1.Y;

            if (isX)
            {
                double dx = x1 - x0;
                if (dx == 0f) return null;
                double t = (bound - x0) / dx;
                if (t < 0f || t > 1f) return null;
                return new Point(bound, y0 + (y1 - y0) * t);
            }
            else
            {
                double dy = y1 - y0;
                if (dy == 0f) return null;
                double t = (bound - y0) / dy;
                if (t < 0f || t > 1f) return null;
                return new Point(x0 + (x1 - x0) * t, bound);
            }
        }

        // JS: clipSegment(p0, p1)
        /// <summary>
        /// Clips a line segment defined by two points to the bounds of a unit square centered at the origin.
        /// </summary>
        /// <remarks>The unit square is defined by the region where both X and Y coordinates are between
        /// -1 and 1, inclusive. If both endpoints are inside the square, the original segment is returned. If both are
        /// outside and the segment crosses the square, the intersection points with the square's edges are returned. If
        /// only one endpoint is inside, the segment is clipped at the intersection with the square's
        /// boundary.</remarks>
        /// <param name="p0">The starting point of the line segment to be clipped.</param>
        /// <param name="p1">The ending point of the line segment to be clipped.</param>
        /// <returns>A tuple containing the endpoints of the clipped segment if the original segment intersects the unit square;
        /// otherwise, <see langword="null"/>.</returns>
        private (Point a, Point b)? ClipSegment(Point p0, Point p1)
        {
            bool Inside(Point p) =>
                p.X >= -1f && p.X <= 1f && p.Y >= -1f && p.Y <= 1f;

            bool p0Inside = Inside(p0);
            bool p1Inside = Inside(p1);

            var bounds = new List<(double bound, bool isX)>
            {
                (-1, true),  // x = -1
                ( 1, true),  // x =  1
                (-1, false), // y = -1
                ( 1, false), // y =  1
            };

            var intersections = new List<Point>();

            foreach (var (bound, isX) in bounds)
            {
                var p = Intersect(p0, p1, bound, isX);
                if (p.HasValue)
                    intersections.Add(p.Value);
            }

            // ケース1：両端が内側 → そのまま
            if (p0Inside && p1Inside)
                return (p0, p1);

            // ケース2：両端が外側
            if (!p0Inside && !p1Inside)
            {
                if (intersections.Count == 2)
                {
                    intersections.Sort((a, b) =>
                    {
                        double ta = (a.X - p0.X) * (a.X - p0.X) + (a.Y - p0.Y) * (a.Y - p0.Y);
                        double tb = (b.X - p0.X) * (b.X - p0.X) + (b.Y - p0.Y) * (b.Y - p0.Y);
                        return ta.CompareTo(tb);
                    });
                    return (intersections[0], intersections[1]);
                }
                return null;
            }

            // ケース3：p0 外 → p1 内
            if (!p0Inside && p1Inside)
            {
                if (intersections.Count > 0)
                {
                    intersections.Sort((a, b) =>
                    {
                        double ta = (a.X - p0.X) * (a.X - p0.X) + (a.Y - p0.Y) * (a.Y - p0.Y);
                        double tb = (b.X - p0.X) * (b.X - p0.X) + (b.Y - p0.Y) * (b.Y - p0.Y);
                        return ta.CompareTo(tb);
                    });
                    var i0 = intersections[0];
                    return (i0, p1);
                }
                return null;
            }

            // ケース4：p0 内 → p1 外
            if (p0Inside && !p1Inside)
            {
                if (intersections.Count > 0)
                {
                    intersections.Sort((a, b) =>
                    {
                        double ta = (a.X - p1.X) * (a.X - p1.X) + (a.Y - p1.Y) * (a.Y - p1.Y);
                        double tb = (b.X - p1.X) * (b.X - p1.X) + (b.Y - p1.Y) * (b.Y - p1.Y);
                        return ta.CompareTo(tb);
                    });
                    var i0 = intersections[0];
                    return (p0, i0);
                }
                return null;
            }

            return null;
        }

        // JS: process() 相当 → 1フレーム分の PCM を返す
        /// <summary>
        /// Generates a buffer containing a sequence of two-dimensional PCM (Pulse Code Modulation) data points for the
        /// current frame, based on the configured set of points and frame duration.
        /// </summary>
        /// <remarks>This method processes a series of points to produce a two-dimensional PCM
        /// representation suitable for audio or signal processing applications. Segments that are too short or clipped
        /// are represented as zeros in the output. The method maintains internal state to ensure continuity across
        /// frames.</remarks>
        /// <returns>An array of floats representing the PCM data for the current frame. The array contains interleaved X and Y
        /// values. If there are insufficient points or the total length is not positive, the array is filled with
        /// zeros.</returns>
        public float[] GenerateXYBuffer()
        {
            float[] pcm = new float[_frameSamples * 2];

            int N = _points.Count;
            if (N < 2 || _totalLength <= 0f)
            {
                // 全部 0
                _skipNextProcess = false;
                return pcm;
            }

            double scale = (_frameSamples / _totalLength) * _speedScale;

            for (int i = 0; i < _frameSamples; i++)
            {
                var p0 = _points[_index];
                var p1 = _points[(_index + 1) % N];

                var clipped = ClipSegment(p0, p1);

                if (clipped == null)
                {
                    _subPos = 0;
                    if (_index + 2 >= N)
                        _skipNextProcess = false;
                    _index = (_index + 2) % N;

                    pcm[i * 2 + 0] = 0f;
                    pcm[i * 2 + 1] = 0f;
                    continue;
                }

                var (a, b) = clipped.Value;
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);

                if (len <= 1e-9f)
                {
                    _subPos = 0;
                    _index = (_index + 2) % N;
                    pcm[i * 2 + 0] = 0f;
                    pcm[i * 2 + 1] = 0f;
                    continue;
                }

                int samplesPerSegment = Math.Max(
                    _minSamplesPerSegment,
                    (int)Math.Floor(len * scale)
                );

                double t = (double)_subPos / (double)samplesPerSegment;

                double x = a.X + dx * t;
                double y = a.Y + dy * t;

                pcm[i * 2 + 0] = (float)x;
                pcm[i * 2 + 1] = (float)y;

                _subPos++;

                if (_subPos >= samplesPerSegment + _blankSamples)
                {
                    _subPos = 0;
                    if (_index + 2 >= N)
                        _skipNextProcess = false;
                    _index = (_index + 2) % N;
                }
            }

            return pcm;
        }

        // OpenAL の Update ループ用
        /// <summary>
        /// Processes audio buffers in the OpenAL update loop to ensure continuous and responsive audio playback.
        /// </summary>
        /// <remarks>Call this method regularly, such as once per frame, to maintain uninterrupted audio
        /// streaming. The method handles unqueuing processed buffers, generating new audio data, and re-queuing the
        /// updated buffers to the audio source. Failure to call this method frequently enough may result in audio
        /// glitches or playback interruptions.</remarks>
        public void Update()
        {
            AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);

            while (processed-- > 0)
            {
                int buf = AL.SourceUnqueueBuffer(_source);

                float[] pcm = GenerateXYBuffer();
                AL.BufferData(buf, ALFormat.StereoFloat32Ext, pcm, _sampleRate);

                AL.SourceQueueBuffer(_source, buf);
            }
        }

        // オプション: パラメータ調整用
        public void SetBlankSamples(int samples) => _blankSamples = Math.Max(0, samples);
        public void SetSpeedScale(float scale) => _speedScale = scale;
        public void SetMinSamplesPerSegment(int v) => _minSamplesPerSegment = Math.Max(1, v);
    }
}
