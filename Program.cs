using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Language.V1;
using Google.Cloud.Speech.V1;

using Grpc.Auth;
using Google.Protobuf.Collections;
using System.Threading;
using NAudio.Mixer;
using System.Diagnostics;
using NAudio.Wave;
using RosSharp.RosBridgeClient;

namespace STT_Bulgarian_Console_app
{
    public enum RecordingState
    {
        Stopped,
        Monitoring,
        Recording,
        RequestedStop
    }

    public class SampleAggregator
    {
        public event EventHandler<MaxSampleEventArgs> MaximumCalculated;
        public event EventHandler Restart = delegate { };
        private float maxValue;
        private float minValue;
        public int NotificationCount { get; set; }
        int count;

        public void RaiseRestart()
        {
            Restart(this, EventArgs.Empty);
        }

        public void Reset()
        {
            count = 0;
            maxValue = minValue = 0;
        }

        public void Add(float value)
        {
            maxValue = Math.Max(maxValue, value);
            minValue = Math.Min(minValue, value);
            count++;
            if (count >= NotificationCount && NotificationCount > 0)
            {
                if (MaximumCalculated != null)
                {
                    MaximumCalculated(this, new MaxSampleEventArgs(minValue, maxValue));
                }
                Reset();
            }
        }
    }

    public class MaxSampleEventArgs : EventArgs
    {
        [DebuggerStepThrough]
        public MaxSampleEventArgs(float minValue, float maxValue)
        {
            MaxSample = maxValue;
            MinSample = minValue;
        }
        public float MaxSample { get; private set; }
        public float MinSample { get; private set; }
    }

    public interface IAudioRecorder
    {
        void BeginMonitoring(int recordingDevice);
        void BeginRecording(string path);
        void Stop();
        double MicrophoneLevel { get; set; }
        RecordingState RecordingState { get; }
        SampleAggregator SampleAggregator { get; }
        event EventHandler Stopped;
        WaveFormat RecordingFormat { get; set; }
        TimeSpan RecordedTime { get; }
    }

    public class AudioRecorder : IAudioRecorder
    {
        WaveInEvent waveIn;
        private SampleAggregator sampleAggregator;
        UnsignedMixerControl volumeControl;
        double desiredVolume = 100;
        RecordingState recordingState;
        WaveFileWriter writer;
        WaveFormat recordingFormat;

        public event EventHandler Stopped = delegate { };

        public AudioRecorder()
        {
            sampleAggregator = new SampleAggregator();
            RecordingFormat = new WaveFormat(44100, 1);
        }

        public WaveFormat RecordingFormat
        {
            get
            {
                return recordingFormat;
            }
            set
            {
                recordingFormat = value;
                sampleAggregator.NotificationCount = value.SampleRate / 10;
            }
        }

        public void BeginMonitoring(int recordingDevice)
        {
            if (recordingState != RecordingState.Stopped)
            {
                throw new InvalidOperationException("Can't begin monitoring while we are in this state: " + recordingState.ToString());
            }

            waveIn = new WaveInEvent();
            waveIn.DeviceNumber = recordingDevice;
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;
            waveIn.WaveFormat = recordingFormat;
            waveIn.StartRecording();
            TryGetVolumeControl();
            recordingState = RecordingState.Monitoring;
        }

        void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            recordingState = RecordingState.Stopped;
            writer?.Dispose();
            Stopped(this, EventArgs.Empty);
        }

        public void BeginRecording(string waveFileName)
        {
            if (recordingState != RecordingState.Monitoring)
            {
                throw new InvalidOperationException("Can't begin recording while we are in this state: " + recordingState.ToString());
            }

            writer = new WaveFileWriter(waveFileName, recordingFormat);
            recordingState = RecordingState.Recording;
        }

        public void Stop()
        {
            if (recordingState == RecordingState.Recording)
            {
                recordingState = RecordingState.RequestedStop;
                waveIn.StopRecording();
            }
            if (recordingState == RecordingState.Monitoring)
            {
                recordingState = RecordingState.Stopped;
                waveIn.StopRecording();
                waveIn.Dispose();
                waveIn = null;
            }
        }

        private void TryGetVolumeControl()
        {
            int waveInDeviceNumber = waveIn.DeviceNumber;
            if (Environment.OSVersion.Version.Major >= 6) // Vista and over
            {
                var mixerLine = waveIn.GetMixerLine();
                //new MixerLine((IntPtr)waveInDeviceNumber, 0, MixerFlags.WaveIn);
                foreach (var control in mixerLine.Controls)
                {
                    if (control.ControlType == MixerControlType.Volume)
                    {
                        this.volumeControl = control as UnsignedMixerControl;
                        MicrophoneLevel = desiredVolume;
                        break;
                    }
                }
            }
            else
            {
                var mixer = new Mixer(waveInDeviceNumber);
                foreach (var destination in mixer.Destinations
                    .Where(d => d.ComponentType == MixerLineComponentType.DestinationWaveIn))
                {
                    foreach (var source in destination.Sources
                        .Where(source => source.ComponentType == MixerLineComponentType.SourceMicrophone))
                    {
                        foreach (var control in source.Controls
                            .Where(control => control.ControlType == MixerControlType.Volume))
                        {
                            volumeControl = control as UnsignedMixerControl;
                            MicrophoneLevel = desiredVolume;
                            break;
                        }
                    }
                }
            }

        }

        public double MicrophoneLevel
        {
            get
            {
                return desiredVolume;
            }
            set
            {
                desiredVolume = value;
                if (volumeControl != null)
                {
                    volumeControl.Percent = value;
                }
            }
        }

        public SampleAggregator SampleAggregator
        {
            get
            {
                return sampleAggregator;
            }
        }

        public RecordingState RecordingState
        {
            get
            {
                return recordingState;
            }
        }

        public TimeSpan RecordedTime
        {
            get
            {
                if (writer == null)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds((double)writer.Length / writer.WaveFormat.AverageBytesPerSecond);
            }
        }

        void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            byte[] buffer = e.Buffer;
            int bytesRecorded = e.BytesRecorded;
            WriteToFile(buffer, bytesRecorded);

            for (int index = 0; index < e.BytesRecorded; index += 2)
            {
                short sample = (short)((buffer[index + 1] << 8) |
                                        buffer[index + 0]);
                float sample32 = sample / 32768f;
                sampleAggregator.Add(sample32);
            }
        }

        private void WriteToFile(byte[] buffer, int bytesRecorded)
        {
            long maxFileLength = this.recordingFormat.AverageBytesPerSecond * 60;

            if (recordingState == RecordingState.Recording || recordingState == RecordingState.RequestedStop)
            {
                var toWrite = (int)Math.Min(maxFileLength - writer.Length, bytesRecorded);
                if (toWrite > 0)
                {
                    writer.Write(buffer, 0, bytesRecorded);
                }
                else
                {
                    Stop();
                }
            }
        }
    }

    class Program
    {

        private static List<string> recordingDevices = new List<string>();
        private static AudioRecorder audioRecorder = new AudioRecorder();

        private static Boolean monitoring = false;

        //private RecognitionConfig oneShotConfig;
        //private SpeechClient speech = SpeechClient.Create();
        //private SpeechClient.StreamingRecognizeStream streamingCall;
        //private StreamingRecognizeRequest streamingRequest;

        private static BufferedWaveProvider waveBuffer;

        static TimerCallback callback = new TimerCallback(Tick);
        private static Timer timer1;

        // Read from the microphone and stream to API.
        private static WaveInEvent waveIn = new NAudio.Wave.WaveInEvent();
        private static bool timer1Enabled = false;

        private static bool sttEnabled = false;
        private static MqttClient mqttClient;

        static public void Tick(Object stateInfo)
        {
            if (timer1Enabled)
            {
                Timer1_Tick();
                
            }
        }

        static void Main(string[] args)
        {
            //https://www.nuget.org/packages/M2Mqtt/
            //Install-Package M2Mqtt -Version 4.3.0

            Console.OutputEncoding = System.Text.Encoding.UTF8;

        // MQTT client setup
        //https://community.openhab.org/t/clearing-mqtt-retained-messages/58221

            // Change default MQTT broker address here
            string BrokerAddress = "192.168.1.2";

            if (args.Length == 1)
            {
                BrokerAddress = args[0];
                System.Console.WriteLine("Setting broker IP to: " + BrokerAddress);
            }
            else
            {
                System.Console.WriteLine("To change the broker IP when starting the app, use: STT_Bulgarian_Console_app.exe \"MQTT_broker_name_or_IP\"");
                System.Console.WriteLine("Setting broker IP to the default: " + BrokerAddress);
            }

            //var client = new MqttClient("192.168.1.2");
            mqttClient = new MqttClient(BrokerAddress);



            // register a callback-function called by the M2mqtt library when a message was received
            mqttClient.MqttMsgPublishReceived += mqttClient_messageReceived;

            var clientId = Guid.NewGuid().ToString();

            //TODO create new user pass at the MQTT broker and replace the one below:
            
            // supply - user, pass for the MQTT broker
            mqttClient.Connect(clientId, "openhabian", "robko123");

            //########### Publish/Subscribe to STT (Speech To Text) Bulgarian topics ROS/MQTT communication ##########
            //# //////////////////////////////////////////////////////////////////////////////////////////////////////
            //# // ROS topics: 
            //# //        /sttbg_ros/stt_text	        String
            //# //        /sttbg_ros/enable		        String  //no Bool on the Windows side? So we make it enable/disable words... 
            //# //        /sttbg_ros/response		    Bool    
            //# // MQTT topics: 
            //# //        /sttbg_mqtt/stt_text	        String
            //# //        /sttbg_mqtt/enable		    String
            //# //        /sttbg_mqtt/response		    Bool
            //# // 
            //# // mqtt_bridge ROS package is set to transfer the messages between ROS and the MQTT broker in both directions
            //# // Topics have to be setup in mqtt_bridge package in /home/robcoctrl/catkin_ws/src/mqtt_bridge/config/openhab_tts_stt_params.yaml
            //# //
            //# ///////////////////////////////////////////////////////////////////////////////////////////////////////

            // Subscribe to the required MQTT topics with QoS 2
            // There is a bug when you specify more than one topic at the same time???
            //https://stackoverflow.com/questions/39917469/exception-of-type-uplibrary-networking-m2mqtt-exceptions-mqttclientexception-w
            //client.Subscribe(
            //    new string[] { "/ttsbg_mqtt/text_to_be_spoken", "/ttsbg_mqtt/command" },
            //    new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
            mqttClient.Subscribe(new string[] { "/sttbg_mqtt/enable" }, new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });

            //TODO
            // Publish enabled response to the MQTT response topic
            // Cant find data type other than String for the MQTT Publisher???? Publish the response as a string?
            // bool enbabled = false;
            //client.Publish("/tts_bg_mqtt/response", enabled, MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE);
            //client.Publish("/tts_bg_mqtt/stt_text", Encoding.UTF8.GetBytes(strResponse), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE);



            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "Proj T2s-89d4c34d98fc.json");

            if (NAudio.Wave.WaveIn.DeviceCount < 1)
            {
                Console.WriteLine("No microphone! ... exiting");
                return;
            }

            //Mixer
            //Hook Up Audio Mic for sound peak detection
            audioRecorder.SampleAggregator.MaximumCalculated += OnRecorderMaximumCalculated;

            for (int n = 0; n < WaveIn.DeviceCount; n++)
            {
                recordingDevices.Add(WaveIn.GetCapabilities(n).ProductName);
            }

            //Set up Google specific code
            //oneShotConfig = new RecognitionConfig();
            //oneShotConfig.Encoding = RecognitionConfig.Types.AudioEncoding.Linear16;
            //oneShotConfig.SampleRateHertz = 16000;
            //oneShotConfig.LanguageCode = "en";



            //Set up NAudio waveIn object and events
            waveIn.DeviceNumber = 0;
            waveIn.WaveFormat = new NAudio.Wave.WaveFormat(16000, 1);
            //Need to catch this event to fill our audio beffer up
            waveIn.DataAvailable += WaveIn_DataAvailable;
            //the actuall wave buffer we will be sending to googles for voice to text conversion
            waveBuffer = new BufferedWaveProvider(waveIn.WaveFormat);
            waveBuffer.DiscardOnBufferOverflow = true;

            //We are using a timer object to fire a one second record interval
            //this gets enabled and disabled based on when we get a peak detection from NAudio
            //timer1Enabled = false;
            //One second record window
            //Hook up to timer tick event


            //button_Click();
        }

        static void OnRecorderMaximumCalculated(object sender, MaxSampleEventArgs e)
        {
            float peak = Math.Max(e.MaxSample, Math.Abs(e.MinSample));

            // multiply by 100 because the Progress bar's default maximum value is 100
            peak *= 100;
            //Console.WriteLine("Recording Level " + peak);
            if (peak > 35)
            {
                //Timer should not be enabled, meaning, we are not already recording
                if (timer1Enabled == false)
                {
                    timer1Enabled = true;
                    audioRecorder.Stop();
                    timer1 = new Timer(callback, null, 3000, -1);
                    //Console.Beep(1000,500);
                    //Console.WriteLine("Timer started");
                    Console.WriteLine("Start recording");
                    waveIn.StartRecording();

                }

            }

        }

        private static void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            waveBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        }

        private static void Timer1_Tick()
        {
            Console.WriteLine("Stop recording");
            //Stop recording
            waveIn.StopRecording();
            //Turn off events, will get re-enabled once another audio peak gets detected
            //Console.Beep();
            //Thread.Sleep(1000);
            timer1Enabled = false;
            timer1?.Dispose();
            timer1 = null;
            //Console.WriteLine("Timer stopped");
            //audioRecorder.SampleAggregator.Reset();
            //Console.Beep(800, 800);
            //Call the async google voice stream method with our saved audio buffer
            Task me = StreamBufferToGooglesAsync();
            try
            {
                //bool complete = me.Wait(5000);
                // Console.WriteLine((!complete ? "Not" : "") + "Complete");
                me.Wait();
            }
            catch
            {

            }
            Console.WriteLine("Talk");
            //Console.Beep();
            audioRecorder.SampleAggregator.Reset();
            audioRecorder.BeginMonitoring(0);
        }

        private static async Task<object> StreamBufferToGooglesAsync()
        {
            //I don't like having to re-create these everytime, but breaking the
            //code out is for another refactoring.
            var speech = SpeechClient.Create();
            var streamingCall = speech.StreamingRecognize();

            // Write the initial request with the config.
            //Again, this is googles code example, I tried unrolling this stuff
            //and the google api stopped working, so stays like this for now
            await streamingCall.WriteAsync(new StreamingRecognizeRequest()
            {
                StreamingConfig = new StreamingRecognitionConfig()
                {
                    Config = new RecognitionConfig()
                    {
                        Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = 16000,
                        LanguageCode = "bg-BG",
                    },

                    //Note: play with this value
                    // InterimResults = true,  // this needs to be true for real time
                    SingleUtterance = true,
                }
            });



            //Get what ever data we have in our internal wave buffer and put into
            //byte array for googles
            byte[] buffer = new byte[waveBuffer.BufferLength];
            int offset = 0;
            int count = waveBuffer.BufferLength;

            //Gulp ... yummy bytes ....
            waveBuffer.Read(buffer, offset, count);
            //Clear our internal wave buffer
            waveBuffer.ClearBuffer();

            try
            {
                //Sending to Googles .... finally
                await streamingCall.WriteAsync(new StreamingRecognizeRequest()
                {
                    AudioContent = Google.Protobuf.ByteString.CopyFrom(buffer, 0, count)
                });
            }
            catch (Exception wtf)
            {
                string wtfMessage = wtf.Message;
            }
            finally
            {
                //Tell googles we are done for now
                await streamingCall.WriteCompleteAsync();
            }
            //Again, this is googles code example below, I tried unrolling this stuff
            //and the google api stopped working, so stays like this for now

            //Print responses as they arrive. Need to move this into a method for cleanslyness
            Task printResponses = Task.Run(async () =>
            {
                string saidWhat = "";
                string lastSaidWhat = "";
                while (await streamingCall.ResponseStream.MoveNext(default(CancellationToken)))
                {
                    foreach (var result in streamingCall.ResponseStream.Current.Results)
                    {
                        foreach (var alternative in result.Alternatives)
                        {
                            saidWhat = alternative.Transcript;
                            if (lastSaidWhat != saidWhat)
                            {
                                lastSaidWhat = saidWhat;
                                //Need to call this on UI thread ....
                                Console.WriteLine(saidWhat.ToLower().Trim() + " \r\n");
                                // TODO Should we retain the message on the broker? reatin - true or false in our case??????????????????????????????????????????????????????
                                // TODO Trim the message text or not???????????


                                //mqttClient.Publish("/sttbg_mqtt/stt_text", Encoding.UTF8.GetBytes(saidWhat.ToLower()), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);
                                mqttClient.Publish("/sttbg_mqtt/stt_text", Encoding.UTF8.GetBytes("��data�test"), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);
                                mqttClient.Publish("/sttbg_mqtt/response", Encoding.UTF8.GetBytes("enabled"), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);
                                mqttClient.Publish("/sttbg_mqtt/stt_text", Encoding.UTF8.GetBytes(saidWhat.ToLower()), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);

                            }

                        }  // end for

                    } // end for

                }
            });

            try
            {
                await printResponses;
            }
            catch
            {

            }

            return 0;
        }

        static void mqttClient_messageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            ////For multiple subscribed topics, put an if statement into the message handler to branch based
            ////on the incoming messages topic e.Topic.
            ////You can write functions to handle the different message types and call these from the message handler
            ////and pass the MqttMsgPublishEventArgs object to those functions.

            //Console.OutputEncoding = System.Text.Encoding.UTF8;

            //// Handle the received message

            //////Console.WriteLine("raw message= " + Encoding.UTF8.GetString(e.Message) + "      on topic " + e.Topic);
            //var stringLenght = Encoding.UTF8.GetString(e.Message).Length;

            ////The MQTT client in ROS includes ' "��data�", ' infront of the message payload
            ////Here we remove 7 symbols - ��data� from the message ("��data�", "") and get only the string payload
            //string messageText = Encoding.UTF8.GetString(e.Message).Substring(9, stringLenght - 9);
            //Console.WriteLine("Text to be spoken received => " + Encoding.UTF8.GetString(e.Message).Substring(9, stringLenght - 9));
            Console.WriteLine("String received  => " + Encoding.UTF8.GetString(e.Message).ToString());
            var stringLenght = Encoding.UTF8.GetString(e.Message).Length;
            //Console.WriteLine("String received on topic /sttbg_mqtt/enable => " + Encoding.UTF8.GetString(e.Message).Substring(9, stringLenght - 9));
            Console.WriteLine("String received on topic /sttbg_mqtt/enable => " + Encoding.UTF8.GetString(e.Message).ToString());
            Console.WriteLine("topic => " + e.Topic.ToString());
            ////Branch based on the incoming messages topic e.Topic.
            if (e.Topic.ToString() == "/sttbg_mqtt/enable")
            {
                //var stringLenght = Encoding.UTF8.GetString(e.Message).Length;
                Console.WriteLine("String received on topic /sttbg_mqtt/enable => " + Encoding.UTF8.GetString(e.Message).Substring(7, stringLenght - 7));
                //Console.WriteLine("String received on topic /sttbg_mqtt/enable => " + Encoding.UTF8.GetString(e.Message).ToString());

                if (recordingDevices.Count > 0)
                {
                    if (monitoring == false && Encoding.UTF8.GetString(e.Message).Substring(7, stringLenght - 7) =="enable")
                    {
                        monitoring = true;
                        //Begin
                        audioRecorder.BeginMonitoring(0);
                        sttEnabled = true;
                    }
                    else if (monitoring == true && Encoding.UTF8.GetString(e.Message).Substring(7, stringLenght - 7) == "disable")
                    {
                        monitoring = false;
                        waveIn.StopRecording();
                        audioRecorder.Stop();
                        //Console.Beep();
                        sttEnabled = false;
                    }
                    else
                    {
                        // pri povtoren enable???
                        monitoring = false;
                        audioRecorder.Stop();
                        

                    }


                }


            }
            //else if (e.Topic == "/sttbg_mqtt/command")
            //{
            //    if (messageText == "cancel")
            //    {
            //        cancelSpeaking();
            //    }
            //}
        }
    }
}
