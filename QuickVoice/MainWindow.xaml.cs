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
                new VoipModule(this).Run(listen, connect, exitTokenSource.Token);
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
    }

    class VoipModule : IWaveProvider
    {
        private WaveIn recorder;
        private WaveOut player;
        private MainWindow mainWindow;
        const int HEADER_LEN = 12;

        public VoipModule(MainWindow mainWindow)
        {
            this.mainWindow = mainWindow;
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

            recorder = new WaveIn(WaveCallbackInfo.FunctionCallback());
            recorder.BufferMilliseconds = 5;
            recorder.NumberOfBuffers = 2;
            recorderStream = tcpClient.GetStream();
            recorder.DataAvailable += MicrophoneDataAvailable;
            recorder.WaveFormat = new WaveFormat(8000, 16, 1);

            player = new WaveOut(WaveCallbackInfo.FunctionCallback());
            player.DesiredLatency = 60;
            player.Init(this);

            player.Play(); // Is no problem, will output zeros until data start rolling in.
            recorder.StartRecording();

            RunTcpReceiver(tcpClient, exitToken);

            player.Stop();
            recorder.StopRecording();

            player = null;
            recorder = null;
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
                float multiplier = BitConverter.ToSingle(header, 4);

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

                for (int i = 0; i < buflen; i++)
                {
                    short sample = (short)((sbyte)(buf[i]) * multiplier);
                    cbuffer[writePointer] = (byte)(sample & 0xff);
                    cbuffer[writePointer + 1] = (byte)((sample >> 8) & 0xff);
                    writePointer = (writePointer + 2) % N;
                    if (writePointer == readPointer)
                    {
                        throw new NotImplementedException("BufferOverrunWrite");
                    }
                }
            }
        }



        const int N = 16000;
        byte[] cbuffer = new byte[N];
        int readPointer = 0;
        int writePointer = 0;
        private NetworkStream recorderStream;

        public WaveFormat WaveFormat => new WaveFormat(8000, 16, 1);

        public int Read(byte[] buffer, int offset, int count)
        {


            for (int i = 0; i < count; i++)
            {
                if (readPointer == writePointer)
                {
                    buffer[i + offset] = 0;
                }
                else
                {
                    buffer[i + offset] = cbuffer[readPointer];
                    readPointer = (readPointer + 1) % N;
                }

            }
            return count;
        }





        private void MicrophoneDataAvailable(object sender, WaveInEventArgs waveInEventArgs)
        {

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

            byte[] bytesToSend = BitConverter.GetBytes((int)sampleCount).Concat(BitConverter.GetBytes(multiplier)).Concat(BitConverter.GetBytes((int)0)).Concat(sendbuf).ToArray();
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

        /*
         
        Packet format:

         0 -  3: BufLen (int32)
         4 -  7: Multiplier (float)
         8 - 11: 
        12 - ..: Samples (signed byte each



    */
    }
}
