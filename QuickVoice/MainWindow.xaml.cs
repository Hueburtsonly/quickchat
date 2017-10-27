using System;
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

namespace QuickVoice
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        Regex patArg = new Regex(@"^--(\w+)=(\S*)$");



        public MainWindow(StartupEventArgs e)
        {

            IPEndPoint listen = null;
            IPEndPoint remote = null;

            string[] args = e.Args;

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
                    case "remote":
                        remote = parseHostAndPort(value);
                        break;
                    default:
                        throw new NotImplementedException("Unknown flag: '" + flag + "'");
                }

            }

            if (listen == null && remote == null)
            {
                //throw new NotImplementedException("Must specify --listen or --remote.");
            }
            if (listen != null && remote != null)
            {
                throw new NotImplementedException("Must specify only one of --listen or --remote.");
            }

            InitializeComponent();

            new Thread(() => new VoipModule().RunMicrophone(listen, remote)).Start();
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
                ipAddress = Dns.GetHostEntry(host).AddressList[0];
            }

            // TODO: hostInfo.IPAddress might be empty.
            return new IPEndPoint(ipAddress, Convert.ToInt32(port));
        }

    }

    class VoipModule : IWaveProvider
    {
        private WaveIn recorder;
        private WaveOut player;

        public void RunMicrophone(IPEndPoint listen, IPEndPoint remote)
        {
            RunMicrophone(null);
            return;
            if (listen != null)
            {
                TcpListener tcpListener = new TcpListener(listen);
                tcpListener.Start();
                TcpClient tcpClient = tcpListener.AcceptTcpClient();
                tcpListener.Stop();
                RunMicrophone(tcpClient);
            }
            else if (remote != null)
            {
                TcpClient tcpClient = new TcpClient();
                tcpClient.Connect(remote);
                RunMicrophone(tcpClient);
            }
            else
            {
                throw new NotImplementedException("Unreachable");
            }

        }

        public void RunMicrophone(TcpClient tcpClient) {
            //tcpClient.NoDelay = true;

            recorder = new WaveIn(WaveCallbackInfo.FunctionCallback());
            recorder.BufferMilliseconds = 5;
            recorder.NumberOfBuffers = 2;
            recorder.DataAvailable += MicrophoneDataAvailable;
            recorder.WaveFormat = new WaveFormat(8000, 16, 1);

            player = new WaveOut(WaveCallbackInfo.FunctionCallback());
            player.DesiredLatency = 60;
            player.Init(this);

            player.Play(); // Is no problem, will output zeros until data start rolling in.

            
            // TODO: Launch TCP -> cbuffer thread.
            

            recorder.StartRecording();
        }



        const int N = 16000;
        byte[] cbuffer = new byte[N];
        int readPointer = 0;
        int writePointer = 0;

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
            //bufferedWaveProvider.AddSamples(waveInEventArgs.Buffer, 0, waveInEventArgs.BytesRecorded);

            /*

            int sampleCount = waveInEventArgs.BytesRecorded / 2;
            float[] samples = new float[sampleCount];
            float max = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)((int)(waveInEventArgs.Buffer[2 * i]) + (((int)(waveInEventArgs.Buffer[2 * i + 1])) << 8));
                float unsignedf = Math.Abs(samples[i]);
                if (unsignedf > max)
                {
                    max = unsignedf;
                }
            }
            */

            byte[] lb = waveInEventArgs.Buffer;

            for (int i = 0; i < waveInEventArgs.BytesRecorded; i++)
            {
                cbuffer[writePointer] = lb[i];
                writePointer = (writePointer + 1) % N;
                if (writePointer == readPointer)
                {

                    Console.WriteLine("BufferOverrunWrite");
                    return;
                }
            }


            // TODO: Send data via TCP instead of looping back.


        }
    }
}
