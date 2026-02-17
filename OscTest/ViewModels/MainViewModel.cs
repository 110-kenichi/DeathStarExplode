using Avalonia;
using OpenTK.Audio.OpenAL;
using OpenTK.Compute.OpenCL;
using OscTest.Services;
using OscTest.Servises;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq; // 追加: IObservable<T>.Subscribe の Action オーバーロードを解決するため
using System.Threading.Tasks;
using ReactiveUI.SourceGenerators;
using System.Numerics;

namespace OscTest.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private ALDevice _alDevice;
    private int _alSource;
    private ALContext _alContext;
    private int[] _sampleBufferIds;
    private readonly XYProcessor _xyProcessor;

    /// <summary>
    /// Initializes a new instance of the MainViewModel class, setting up audio processing and playback using default
    /// parameters.
    /// </summary>
    /// <remarks>This constructor configures the audio device and context, initializes the XYProcessor with a
    /// default set of points, generates initial audio buffers, and starts audio playback. It also sets up a timer to
    /// periodically update audio processing. The initial state includes a trigger for a specific audio event. The
    /// caller does not need to perform additional setup after instantiation.</remarks>
    public MainViewModel()
    {
        _alDevice = ALC.OpenDevice(null);
        ALContextAttributes a = new ALContextAttributes();
        _alContext = ALC.CreateContext(_alDevice, a);
        ALC.MakeContextCurrent(_alContext);

        _alSource = AL.GenSource();
        _sampleBufferIds = AL.GenBuffers(4);
        int sampleRate = 48000;
        if (a.Frequency.HasValue)
            sampleRate = a.Frequency.Value;

        _xyProcessor = new XYProcessor(_alSource, sampleRate, sampleRate / 60);

        // ポイントをセット（JS の setPoints と同じ感覚）
        _xyProcessor.SetPoints(new List<Point>
        {
            new Point(0, 0), new Point(1, 1),
            new Point(1,-1), new Point(-1,-1),
            new Point(-1,1), new Point(0,0),
        });

        // 初期バッファを埋める
        foreach (var b in _sampleBufferIds)
        {
            var pcm = _xyProcessor.GenerateXYBuffer();
            AL.BufferData(b, ALFormat.StereoFloat32Ext, pcm, sampleRate);
            AL.SourceQueueBuffer(_alSource, b);
        }

        AL.SourcePlay(_alSource);

        //audio = new XYOscilloscopeAudio();
        //audio.Init();

        var TimerObservable = Observable.Interval(TimeSpan.FromMilliseconds(5));
        TimerObservable.Subscribe(x =>
        {
            pts = new List<Point>();
            if (shock != null)
            {
                pts = shock.BuildPoints();
                shock.Update(0.016f);
                switch (shock.Phase)
                {
                    case 0:
                        if (shock.Radius > 0.6)
                        {
                            shock.Radius = 0.1;
                            shock.Phase++;
                        }
                        break;
                    case 1:
                        if (shock.Radius > 0.8)
                        {
                            shock.Radius = 0.3;
                            shock.Phase++;
                        }
                        break;
                    case 2:
                        if (shock.Radius > 1.0)
                        {
                            shock.Radius = 0.0;
                            shock.CoresOffset += 0.1;
                            shock.Phase++;
                        }
                        break;

                    case 3:
                        if (shock.Radius > 0.7)
                        {
                            shock.Radius = 0.2;
                            shock.Phase++;
                        }
                        break;
                    case 4:
                        if (shock.Radius > 0.9)
                        {
                            shock.Radius = 0.4;
                            shock.Phase++;
                        }
                        break;
                    case 5:
                        if (shock.Radius > 1.0)
                        {
                            shock.Radius = 0.1;

                            shock.Rings *= 5;
                            shock.RingSpace /= 2;
                            shock.Speed = 0.15;

                            shock.Cores = 0;
                            //shock.CoresOffset += 0.1;
                            shock.Phase++;
                        }
                        break;
                    case 6:
                        if (shock.Radius > 0.5)
                        {
                            shock.Speed = 0.40;
                            shock.Phase++;
                        }
                        break;
                    case 7:
                        if (shock.Radius > 1.2)
                        {
                            shock = null;
                            pts.Clear();
                        }
                        break;
                }
            }
            _xyProcessor.SetPoints(pts);
            _xyProcessor.Update();
        }).DisposeWith(_disposables);
    }

    private List<Point> pts = new List<Point>();

    private Shockwave? shock;

    //https://github.com/reactiveui/ReactiveUI.SourceGenerators

    [ReactiveCommand]
    public void SpawnDeathStarExplosion()
    {
        // 爆発開始トリガ
        shock = new Shockwave();
    }

    public class Shockwave
    {
        public int Phase = 0;

        public double Radius = 0.0;
        public double Speed = 0.75;
        public int Rings = 3;
        public double RingSpace = 0.025;
        public int Cores = 10;
        public double CoresOffset = 0;

        public void Update(double dt)
        {
            Radius += Speed * dt;
        }

        public List<Point> BuildPoints()
        {
            //double alpha = Life / MaxLife; // 1 → 0

            int segments = 24;
            var pts = new List<Point>();

            //ショック
            for (int i = 0; i < Rings; i++)
                pts.AddRange(BuildCircle(CoresOffset + Radius + (RingSpace * (double)i), segments));

            //コア
            for (int i = 1; i <= Cores; i++)
            {
                pts.AddRange(BuildCircle(CoresOffset + 0.01 * i, segments));
            }

            return pts;
        }


        private List<Point> BuildCircle(double radius, int segments)
        {
            var pts = new List<Point>();

            for (int i = 0; i < segments; i++)
            {
                double a0 = (double)(Math.PI * 2 * i / segments);
                double a1 = (double)(Math.PI * 2 * (i + 1) / segments);

                double x0 = radius * Math.Cos(a0);
                double y0 = radius * Math.Sin(a0);
                double x1 = radius * Math.Cos(a1);
                double y1 = radius * Math.Sin(a1);

                pts.Add(new Point(x0, y0));
                pts.Add(new Point(x1, y1));
            }

            return pts;
        }
    }

    private readonly CompositeDisposable _disposables = new();

    public void Dispose()
    {
        _disposables.Dispose();

        try
        {
            if (_alSource != 0)
            {
                AL.SourceStop(_alSource);
                AL.DeleteSource(_alSource);
                _alSource = 0;
            }

            if (_sampleBufferIds != null)
            {
                AL.DeleteBuffers(_sampleBufferIds);
            }

            if (_alContext != ALContext.Null)
            {
                ALC.MakeContextCurrent(ALContext.Null);
                ALC.DestroyContext(_alContext);
                _alContext = ALContext.Null;
            }

            if (_alDevice != ALDevice.Null)
            {
                ALC.CloseDevice(_alDevice);
                _alDevice = ALDevice.Null;
            }

        }
        catch
        {
            // 破棄時の例外は握りつぶす
        }
    }

}
