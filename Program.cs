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

//#     #################################
//#     # 
//#     # Created by Ivan, Milko, Stefan 
//#     # SRG - Service Robotics Group Bulgaria
//#     # Version 1.0 from Apr. 6th, 2019.
//#     #
//#     # Based on:  https://github.com/stevecox1964/WinRecognize
//#     # https://github.com/eclipse/paho.mqtt.m2mqtt/tree/master/M2Mqtt, http://wiki.ros.org/mqtt_bridge
//#     #################################
//#     # If cloning from GitHub, You can find the required Google credentials file
//#     # in the SRG shared directory:  Proj T2s-89d4c34d98fc.json
//#     #
//#     # You can generate your own credentials file at Google cloud and replace in:
//#     # Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "Proj T2s-89d4c34d98fc.json");
//#     #################################

//#     #################################
//#     # On the ROS side:
//#     # Set serializer/deserializer in mqtt_bridge: /home/robcoctrl/catkin_ws/src/mqtt_bridge/config/openhab_tts_stt_params.yaml
//#     #     to:   serializer: json:dumps        deserializer: json: loads

//########### Publish/Subscribe to STT (Speech To Text) Bulgarian topics ROS/MQTT communication ##########
//# //////////////////////////////////////////////////////////////////////////////////////////////////////
//# // ROS topics: 
//# //        /sttbg_ros/stt_text	        String
//# //        /sttbg_ros/enable		        String  enable/disable 
//# //        /sttbg_ros/response		    String    
//# // MQTT topics: 
//# //        /sttbg_mqtt/stt_text	        String
//# //        /sttbg_mqtt/enable		    String
//# //        /sttbg_mqtt/response		    String
//# // 
//# // The mqtt_bridge ROS package is set to transfer the messages between ROS and the MQTT broker between the corrresponding topics
//# // These topics are configured in ROS mqtt_bridge package, in /home/<your user>/catkin_ws/src/mqtt_bridge/config/openhab_tts_stt_params.yaml
//# // In the same config file, set serializer/deserializer for mqtt_bridge to:   serializer: json:dumps        deserializer: json: loads
//# ///////////////////////////////////////////////////////////////////////////////////////////////////////
//https://www.nuget.org/packages/M2Mqtt/
//Install-Package M2Mqtt -Version 4.3.0


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

        private static BufferedWaveProvider waveBuffer;

        static TimerCallback callback = new TimerCallback(Tick);
        private static Timer timer1;

        // Read from the microphone and stream to API.
        private static WaveInEvent waveIn = new NAudio.Wave.WaveInEvent();
        private static bool timer1Enabled = false;

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

            Console.OutputEncoding = System.Text.Encoding.UTF8;

        // MQTT client setup
        //https://community.openhab.org/t/clearing-mqtt-retained-messages/58221

            // Set the default MQTT broker address here:
            string BrokerAddress = "192.168.1.2";

            if (args.Length == 1)
            {
                BrokerAddress = args[0];
                System.Console.WriteLine("Setting MQTT broker IP to: " + BrokerAddress);
            }
            else
            {
                System.Console.WriteLine("To change the broker IP, when starting the app use: STT_Bulgarian_Console_app.exe \"MQTT_broker_name_or_IP\"");
                System.Console.WriteLine("Setting MQTT broker IP to the default: " + BrokerAddress);
            }

            mqttClient = new MqttClient(BrokerAddress);



            // register a callback-function called by the M2mqtt library when a message was received
            mqttClient.MqttMsgPublishReceived += mqttClient_messageReceived;

            var clientId = Guid.NewGuid().ToString();

            //TODO create new user pass at the MQTT broker and replace the one below:
            
            // supply - user, pass for the MQTT broker
            mqttClient.Connect(clientId, "openhabian", "robko123");



            // There is a bug when you specify more than one topic at the same time ???
            //https://stackoverflow.com/questions/39917469/exception-of-type-uplibrary-networking-m2mqtt-exceptions-mqttclientexception-w
            //client.Subscribe(
            //    new string[] { "/ttsbg_mqtt/text_to_be_spoken", "/ttsbg_mqtt/command" },
            //    new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });

            // Subscribe to  MQTT topics with QoS 2
            mqttClient.Subscribe(new string[] { "/sttbg_mqtt/enable" }, new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });

            //TODO
            // Add get_status command and /sttbg_mqtt/command topic, and publish back a response to the MQTT response topic:
            //mqttClient.Publish("/sttbg_mqtt/response", Encoding.UTF8.GetBytes("{\"data\": \"" + "enabled" + "\"}"), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);
            //mqttClient.Publish("/sttbg_mqtt/response", Encoding.UTF8.GetBytes("{\"data\": \"" + "disabled" + "\"}"), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);


            //Supply Google credentials
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


            //Set up NAudio waveIn object and events
            waveIn.DeviceNumber = 0;
            waveIn.WaveFormat = new NAudio.Wave.WaveFormat(16000, 1);
            //Need to catch this event to fill our audio beffer up
            waveIn.DataAvailable += WaveIn_DataAvailable;
            //the actuall wave buffer we will be sending to googles for voice to text conversion
            waveBuffer = new BufferedWaveProvider(waveIn.WaveFormat);
            waveBuffer.DiscardOnBufferOverflow = true;

        }

        static void OnRecorderMaximumCalculated(object sender, MaxSampleEventArgs e)
        {
            float peak = Math.Max(e.MaxSample, Math.Abs(e.MinSample));

            // multiply by 100 because the default maximum value is 100
            peak *= 100;
            //Console.WriteLine("Recording Level " + peak);
            if (peak > 35)
            {
                //We are using a timer object to fire a 3 second record interval
                //this gets enabled and disabled based on when we get a peak detection from NAudio

                //Here Timer should not be enabled, meaning, we are not already recording
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
            Console.WriteLine("Listening - Say Robco, followed by a command.");
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

            //Read the buffer
            waveBuffer.Read(buffer, offset, count);
            //Clear our internal wave buffer
            waveBuffer.ClearBuffer();

            try
            {
                //Sending to Google for STT 
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
                //Tell Google we are done for now
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
                                Console.WriteLine(saidWhat.ToLower().Trim() + " \r\n");

                                // TODO Trim the message text or not???????????

                                string myString = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(saidWhat));
                                // Send the recognized text to the MQTT topic    
                                mqttClient.Publish("/sttbg_mqtt/stt_text", Encoding.UTF8.GetBytes("{\"data\": \"" + myString.ToLower() + "\"}"), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, true);

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




            ////Branch based on the incoming messages topic e.Topic.
            if (e.Topic.ToString() == "/sttbg_mqtt/enable")
            {
                //Console.WriteLine("String received on topic /sttbg_mqtt/enable => " + Encoding.UTF8.GetString(e.Message).ToString());

                if (recordingDevices.Count > 0)
                {
                    if (monitoring == false && Encoding.UTF8.GetString(e.Message).ToString() == "{\"data\": \"enable\"}")

                    {
                        monitoring = true;
                        //Begin
                        audioRecorder.BeginMonitoring(0);
                        Console.WriteLine("Speach recognition ENABLED!");
                    }
                    else if (monitoring == true && Encoding.UTF8.GetString(e.Message).ToString() == "{\"data\": \"disable\"}")
                    {
                        monitoring = false;
                        waveIn.StopRecording();
                        audioRecorder.Stop();
                        //Console.Beep();
                        Console.WriteLine("Speach recognition DISABLED!");
                    }
                    else
                    {
                        // Got second ENABLE command??? Should not happen, but process anyways...
                        // Disable Speech recognition, assuming this was the intent, just the wrong button was pressed by the operator.
                        monitoring = false;
                        waveIn.StopRecording();
                        audioRecorder.Stop();
                        //Console.Beep();
                        Console.WriteLine("Speach recognition DISABLED!");
                    }


                }


            }
        }
    }
}
