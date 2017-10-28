﻿using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using NAudio.Wave;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace QuickVoice
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        Regex patArg = new Regex(@"^--(\w+)=(\S*)$");

        public MainWindow(StartupEventArgs ee)
        {

            IPEndPoint listen = null;
            IPEndPoint connect = null;

            string[] args = ee.Args;

            for (int i = 0; i < args.Length; i++)
            {
                Match match = patArg.Match(args[i]);
                if (!match.Success)
                {
                    throw new NotImplementedException("Invalid flaggish thing: '" + args[i] + "'");
                }
                string flag = match.Groups[1].ToString();
                string value = match.Groups[2].ToString();
                switch (flag.ToLower())
                {
                    case "listen":
                        listen = parseHostAndPort(value);
                        break;
                    case "connect":
                        connect = parseHostAndPort(value);
                        break;
                    default:
                        throw new NotImplementedException("Unknown flag: '" + flag + "'");
                }

            }

            if (listen == null && connect == null)
            {
                throw new NotImplementedException("Must specify --listen or --connect.");
            }
            if (listen != null && connect != null)
            {
                throw new NotImplementedException("Must specify only one of --listen or --connect.");
            }

            InitializeComponent();

            var exitTokenSource = new CancellationTokenSource();

            new Thread(() =>
            {
                new VoipModule(this, 44100).Run(listen, connect, exitTokenSource.Token);
                try
                {
                    this.Dispatcher.Invoke(() => Close());
                }
                catch (TaskCanceledException)
                {
                    // Eh.
                }
            }).Start();

            Closing += (s, e) => { exitTokenSource.Cancel(); };
        }


        Regex patHostPort = new Regex(@"^\s*([a-zA-Z0-9.]+):([0-9]+)\s*$");
        Regex patPort = new Regex(@"^\s*([0-9]+)\s*$");
        Regex patHost = new Regex(@"^\s*([a-zA-Z0-9.]+)\s*$");

        private IPEndPoint parseHostAndPort(string hostAndPort)
        {
            string host = null;
            string port = "3456";

            Match match;
            if ((match = patHostPort.Match(hostAndPort)).Success)
            {
                host = match.Groups[1].ToString();
                port = match.Groups[2].ToString();
            }
            else if ((match = patPort.Match(hostAndPort)).Success)
            {
                port = match.Groups[1].ToString();
            }
            else if ((match = patHost.Match(hostAndPort)).Success)
            {
                host = match.Groups[1].ToString();
            }
            else
            {
                throw new NotImplementedException("Couldn't parse " + hostAndPort);
            }

            IPAddress ipAddress = IPAddress.Any;
            if (host != null)
            {
                foreach (IPAddress candidate in Dns.GetHostEntry(host).AddressList)
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ipAddress = candidate;
                        break;
                    }
                }
            }

            // TODO: hostInfo.IPAddress might be empty.
            return new IPEndPoint(ipAddress, Convert.ToInt32(port));
        }

        public void updateStatus(string status)
        {
            try
            {
                this.Dispatcher.Invoke(() => lblStatus.Content = status);
            }
            catch (TaskCanceledException)
            {
                // Eh.
            }
        }

        public void updateStatus2(string status)
        {
            try
            {
                this.Dispatcher.Invoke(() => lblStatus2.Content = status);
            }
            catch (TaskCanceledException)
            {
                // Eh.
            }
        }
    }

    class VoipModule : IWaveProvider
    {
        private Stopwatch stopwatch;
        private WaveIn recorder;
        private WaveOut player;
        private MainWindow mainWindow;
        const int HEADER_LEN = 12;
        int FS;

        public VoipModule(MainWindow mainWindow, int sampleRate)
        {
            this.mainWindow = mainWindow;
            this.FS = sampleRate;
        }

        public void Run(IPEndPoint listen, IPEndPoint connect, CancellationToken exitToken)
        {
            if (listen != null)
            {
                TcpListener tcpListener = new TcpListener(listen);
                tcpListener.Start();
                exitToken.Register(() => { tcpListener.Stop(); });
                while (!exitToken.IsCancellationRequested)
                {
                    mainWindow.updateStatus(string.Format("Listening for connections..."));
                    TcpClient tcpClient;
                    try
                    {
                        tcpClient = tcpListener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    Run(tcpClient, exitToken);
                    mainWindow.updateStatus(string.Format("Disconnected."));
                }
            }
            else if (connect != null)
            {
                for (;;)
                {
                    mainWindow.updateStatus(string.Format("Connecting to {0}:{1}...", connect.Address, connect.Port));
                    TcpClient tcpClient = new TcpClient(connect.AddressFamily);
                    try
                    {
                        tcpClient.Connect(connect);
                        Run(tcpClient, exitToken);
                        mainWindow.updateStatus(string.Format("Disconnected."));
                    }
                    catch (SocketException e)
                    {
                        mainWindow.updateStatus(string.Format("Failed to connect: {0}", e.Message));
                    }
                    if (exitToken.IsCancellationRequested)
                    {
                        break;
                    }
                    Thread.Sleep(1000);
                }
            }
            else
            {
                throw new NotImplementedException("Unreachable");
            }
        }

        public void Run(TcpClient tcpClient, CancellationToken exitToken)
        {
            mainWindow.updateStatus(string.Format("Connected."));
            tcpClient.NoDelay = true;
            exitToken.Register(() => tcpClient.Close());



            stopwatch = Stopwatch.StartNew();

            queue = new ConcurrentQueue<short>();

            recorder = new WaveIn(WaveCallbackInfo.FunctionCallback());
            bytesRecorded = 0;
            recorder.BufferMilliseconds = 10;
            recorder.NumberOfBuffers = 2;
            recorderStream = tcpClient.GetStream();
            recorder.DataAvailable += MicrophoneDataAvailable;
            recorder.WaveFormat = new WaveFormat(FS, 16, 1);

            player = new WaveOut(WaveCallbackInfo.FunctionCallback());
            player.DesiredLatency = 60;
            bytesPlayed = player.DesiredLatency * -FS / 500;
            player.Init(this);

            player.Play(); // Is no problem, will output zeros until data start rolling in.
            recorder.StartRecording();


            new Thread(() =>
             {
                 try
                 {


                     for (;;)
                     {
                         Thread.Sleep(100);

                         int msDifference = (int)(localBytesDifference * (1000 / 2) / FS);
                         mainWindow.updateStatus2(String.Format("{0}ms -- {1}% -- {2} -- {3} -> {4}", msDifference, ((queue.Count * 100) / N), playbackBytesInjected + playbackBytesDiscarded, playbackBytesDiscarded, playbackBytesInjected));

                     }
                 }
                 catch (Exception)
                 { }
             }).Start();


            RunTcpReceiver(tcpClient, exitToken);
            player.Stop();
            recorder.StopRecording();

            player = null;
            recorder = null;
            queue = null;

            tcpClient.Close();
        }

        private void RunTcpReceiver(TcpClient tcpClient, CancellationToken exitToken)
        {
            NetworkStream stream = tcpClient.GetStream();
            exitToken.Register(() => { stream.Close(); });

            byte[] header = new byte[HEADER_LEN];
            byte[] buf = new byte[10000];

            while (!exitToken.IsCancellationRequested)
            {

                int rem = HEADER_LEN;
                int pos = 0;
                do
                {
                    try
                    {
                        int tr = stream.Read(header, pos, rem);
                        if (tr == 0) return;
                        pos += tr;
                        rem -= tr;
                    }
                    catch (IOException)
                    {
                        return;
                    }

                } while (rem != 0);

                int buflen = BitConverter.ToInt32(header, 0);
                float multiplier = BitConverter.ToSingle(header, 4) * 1.5f;

                if (buflen > 10000)
                {
                    throw new NotImplementedException("buflen > 10000!?");
                }

                rem = buflen;
                pos = 0;
                do
                {
                    try
                    {
                        int tr = stream.Read(buf, pos, rem);
                        if (tr == 0) return;
                        pos += tr;
                        rem -= tr;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                } while (rem != 0);

                //Console.WriteLine("Greetz {0} {1}", queue.Count, buflen);

                lock (queue)
                {
                    for (int i = 0; i < buflen; i++)
                    {
                        short sample = (short)((sbyte)(buf[i]) * multiplier);
                        queue.Enqueue(sample);
                        if (queue.Count > N)
                        {
                            Console.WriteLine("BufferOverrun");
                            return;
                        }
                    }
                }
            }
        }


        const int N = 20000;
        ConcurrentQueue<short> queue;
        private NetworkStream recorderStream;

        public WaveFormat WaveFormat => new WaveFormat(FS, 16, 1);

        const int RDT = 15;
        int discardTimeout = RDT;
        public int Read(byte[] buffer, int offset, int count)
        {
            bytesPlayedTs = stopwatch.ElapsedTicks;
            bytesPlayed += count;
            lock (queue)
            {
                for (int i = 0; i < count; i += 2)
                {
                    short sample;
                    if (queue.TryDequeue(out sample))
                    {
                        buffer[i + offset] = (byte)(sample & 0xff);
                        buffer[i + offset + 1] = (byte)((sample >> 8) & 0xff);
                    }
                    else
                    {
                        buffer[i + offset] = 0;
                        buffer[i + offset + 1] = 0;
                        playbackBytesInjected += 2;
                        //Console.WriteLine("INJECT " + stopwatch.ElapsedMilliseconds);
                    }

                }
                if (queue.Count < 16)
                {
                    discardTimeout = RDT;
                }
                else if (discardTimeout > 0)
                {
                    --discardTimeout;
                }
                else
                {
                    short discard;
                    if (queue.TryDequeue(out discard))
                    {
                        playbackBytesInjected -= 2;
                        playbackBytesDiscarded += 2;
                        //Console.WriteLine("DISCARD " + stopwatch.ElapsedMilliseconds);
                    }
                }


            }
            return count;
        }


        int divider = 0;
        int bytesRecorded = 0;
        long bytesRecordedTs = 0;
        long bytesPlayed = 0;
        long bytesPlayedTs = 0;
        int playbackBytesInjected = 0;
        int playbackBytesDiscarded = 0;
        volatile int localBytesDifference = 0;

        private void MicrophoneDataAvailable(object sender, WaveInEventArgs waveInEventArgs)
        {
            bytesRecordedTs = stopwatch.ElapsedTicks;
            bytesRecorded += waveInEventArgs.BytesRecorded;

            //if (++divider == 20)
            {
                long estPos = bytesPlayed + (((stopwatch.ElapsedTicks - bytesPlayedTs) * FS * 2 * 1) / Stopwatch.Frequency);
                localBytesDifference = (int)(bytesRecorded - (estPos - playbackBytesInjected));

                //divider = 0;
            }

            int sampleCount = waveInEventArgs.BytesRecorded / 2;
            float[] samples = new float[sampleCount];
            byte[] sendbuf = new byte[sampleCount];
            float multiplier = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)((int)(waveInEventArgs.Buffer[2 * i]) + (((int)(waveInEventArgs.Buffer[2 * i + 1])) << 8));
                float unsignedf = Math.Abs(samples[i]);
                if (unsignedf > multiplier)
                {
                    multiplier = unsignedf;
                }
            }
            multiplier /= 125;
            for (int i = 0; i < sampleCount; i++)
            {
                sendbuf[i] = (byte)(sbyte)((samples[i] / multiplier) + 0.5);
            }

            var enumerable = BitConverter.GetBytes((int)sampleCount).Concat(BitConverter.GetBytes(multiplier)).Concat(BitConverter.GetBytes((int)0)).Concat(sendbuf);



            byte[] bytesToSend = enumerable.ToArray();
            try
            {
                recorderStream.Write(bytesToSend, 0, bytesToSend.Length);
            }
            catch (IOException)
            {
                // Do nothing; the TCP receiver loop should break soon so the recording will stop soon enough as well.
            }
            catch (ObjectDisposedException)
            {
                // Do nothing; the TCP receiver loop should break soon so the recording will stop soon enough as well.
            }
        }
    }
}


/*

Packet format:

 0 -  3: BufLen (int32)
 4 -  7: Multiplier (float)
 8 - 11: 
12 - ..: Samples (signed byte each

*/
