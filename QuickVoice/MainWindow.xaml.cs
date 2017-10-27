using System;
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

namespace QuickVoice
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {


        public MainWindow(StartupEventArgs e)
        {
            InitializeComponent();

            new Thread(() => new MyProvider().RunMicrophone()).Start();
        }

    }

    class MyProvider : IWaveProvider
    {

        private WaveIn recorder;
        private WaveOut player;

        public WaveFormat WaveFormat => new WaveFormat(8000, 16, 1);


        const int N = 16000;
        byte[] cbuffer = new byte[N];
        int readPointer = 0;
        int writePointer = 0;


        public int Read(byte[] buffer, int offset, int count)
        {


            for (int i = 0; i < count; i++)
            {
                if (readPointer == writePointer)
                {
                    buffer[i + offset] = 0;
                } else { 
                    buffer[i + offset] = cbuffer[readPointer];
                    readPointer = (readPointer + 1) % N;
                }

            }
            return count;
        }


        public void RunMicrophone()
        {
            player = new WaveOut(WaveCallbackInfo.FunctionCallback());
            player.DesiredLatency = 60;
            //player.OutputWaveFormat = new WaveFormat(8000, 16, 1);
            player.Init(this);

            ;
            Console.WriteLine("Hello {0} {1} {2}", player.OutputWaveFormat.SampleRate, player.OutputWaveFormat.BitsPerSample, player.OutputWaveFormat.Channels);

            player.Play();


            recorder = new WaveIn(WaveCallbackInfo.FunctionCallback());
            recorder.BufferMilliseconds = 5;
            recorder.NumberOfBuffers = 2;
            recorder.DataAvailable += MicrophoneDataAvailable;
            recorder.WaveFormat = new WaveFormat(8000, 16, 1);

            //Console.WriteLine("Hello {0}", recorder.WaveFormat.BlockAlign);

            recorder.StartRecording();
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



        }
    }
}
