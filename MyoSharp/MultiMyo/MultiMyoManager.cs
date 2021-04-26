using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using MyoSharp.Communication;
using MyoSharp.Device;
using MyoSharp.Exceptions;
using Clifton.Tools.Data;               // For simple moving average

///-----------------------------------------------------------------
///   Description:    Class used to facilitate all data collection of Myo Armband through the MyoSharp library.  Works for multiple
///                   Myo's.  Streams EMG data, IMU data (pitch roll yaw), raw accelerometer data, and raw gyroscope data
///   Author:         Eric Wells                  
///   Date: 2020-11-06
///----------------------------------------------------------------- 

namespace MyoSharp.MultiMyo
{
    public class MultiMyoManager
    {
        IHub hub;
        IChannel channel;
        static int maxMyoNum = 4;   // max number of myos connected

        int window = 40;     // The window size for the averaging filters i.e. the number of samples it averages over
        public Clifton.Tools.Data.IMovingAverage[,] cliffton_emg_avg = new Clifton.Tools.Data.SimpleMovingAverage[maxMyoNum, 8];    // for simplemovingaverage filter

        // Queue variables to hold data history
        int maxQueueLen = 10;
        public List<List<Queue<float>>> emg_data = new List<List<Queue<float>>>();    // first list: myo number, second list: emg channel, queue: data history

        /// <summary>
        /// pulls current data based on emg_queue_indices
        /// </summary>
        public List<List<float>> Current_emg_data
        {
            get
            {
                List<List<float>> curdata = new List<List<float>>();
                lock (lock_obj)
                {
                    for (int i = 0; i < ID_list.Count; i++)
                    {
                        curdata.Add(new List<float>());
                        for (int j = 0; j < 8; j++)
                        {
                            curdata[i].Add(emg_data[i][j].ElementAt(emg_queue_ind[i]));
                        }
                    }
                }
                return curdata;
            }
        }

        /// <summary>
        /// pulls current accelerometer data
        /// </summary>
        public List<List<double>> Current_acc_data
        {
            get
            {
                List<List<double>> curdata = new List<List<double>>();
                lock (lock_obj)
                {
                    for (int i = 0; i < ID_list.Count; i++)
                    {
                        curdata.Add(new List<double>());
                        for (int j = 0; j < 3; j++)
                        {
                            curdata[i].Add(acc_data[i,j]);
                        }
                    }
                }
                return curdata;
            }
        }

        /// <summary>
        /// pulls current gyroscope data
        /// </summary>
        public List<List<double>> Current_gyro_data
        {
            get
            {
                List<List<double>> curdata = new List<List<double>>();
                lock (lock_obj)
                {
                    for (int i = 0; i < ID_list.Count; i++)
                    {
                        curdata.Add(new List<double>());
                        for (int j = 0; j < 3; j++)
                        {
                            curdata[i].Add(gyro_data[i, j]);
                        }
                    }
                }
                return curdata;
            }
        }

        /// <summary>
        /// pulls average emg data from queue's
        /// </summary>
        public List<List<float>> Avg_emg_data
        {
            get
            {
                List<List<float>> avg_data = new List<List<float>>();
                lock (lock_obj)
                {
                    for (int i=0; i<ID_list.Count; i++)
                    {
                        avg_data.Add(new List<float>());
                        for (int j = 0; j < 8; j++)
                        {
                            avg_data[i].Add(emg_data[i][j].Average());
                        }
                    }
                }
                return avg_data;
            }
        }

        public List<long> ID_list = new List<long>();
        public List<int> emg_queue_ind = new List<int>();
        Thread t;

        // variables to hold data
        public double[,] imu_data = new double[maxMyoNum, 3];
        public double[,] acc_data = new double[maxMyoNum, 3];
        public double[,] gyro_data = new double[maxMyoNum, 3];

        // data stream flags
        bool emgStreamFlag = false;
        bool imuStreamFlag = false;
        bool accStreamFlag = false;
        bool gyroStreamFlag = false;

        // timer for sample rate analyzing
        Stopwatch sw = Stopwatch.StartNew();
        public bool buzzflag = false;

        // lock for lock-safe data reading
        public object lock_obj = new object();

        public MultiMyoManager()
        {

            // initialize moving average values
            for (int i = 0; i < 8; i++)
                {
                for (int j = 0; j < maxMyoNum; j++)
                    {
                    cliffton_emg_avg[j, i] = new Clifton.Tools.Data.SimpleMovingAverage(window);
                    cliffton_emg_avg[j, i].AddSample(0);
                    }
                }

            // initialize Queue's
            for (int i = 0; i < maxMyoNum; i++)
            {
                List<float> temp_list = new List<float>();
                List<Queue<float>> temp_list_queue = new List<Queue<float>>();

                for (int j = 0; j < 8; j++)
                {
                    temp_list.Add(0);

                    Queue<float> temp_queue = new Queue<float>();
                    for (int k = 0; k < maxQueueLen; k++)
                    {
                        temp_queue.Enqueue(0);
                    }
                    temp_list_queue.Add(temp_queue);
                }
                emg_data.Add(temp_list_queue);
                //Current_emg_data.Add(temp_list);
                emg_queue_ind.Add(0);
            }
        }

        public void start_streaming(bool _emgStreamFlag, bool _imuStreamFlag, bool _accStreamFlag, bool _gyroStreamFlag)
        {
            emgStreamFlag = _emgStreamFlag;
            imuStreamFlag = _imuStreamFlag;
            accStreamFlag = _accStreamFlag;
            gyroStreamFlag = _gyroStreamFlag;

            // clear myoIDlist
            ID_list.Clear();

            // create myosharp hub, add connect and disconnect functions, start listening for data
            channel = MyoSharp.Communication.Channel.Create(
                    ChannelDriver.Create(ChannelBridge.Create(), MyoErrorHandlerDriver.Create(MyoErrorHandlerBridge.Create())));
            hub = Hub.Create(channel);
            hub.MyoConnected += Hub_MyoConnected;
            hub.MyoDisconnected += Hub_MyoDisconnected;
            channel.StartListening();

            t = new Thread(emg_thread_loop);
            t.Start();
        }

        public void stop_streaming()
        {
            // stop listening for data, delete connect and disconnect functions
            channel.StopListening();
            hub.MyoConnected -= Hub_MyoConnected;
            hub.MyoDisconnected -= Hub_MyoDisconnected;
            t.Abort();
            t = null;
        }

        private void Hub_MyoConnected(object sender, MyoEventArgs e)
        {
            e.Myo.Vibrate(VibrationType.Medium);

            // add ID of myo to the IDList
            long handle = (long)e.Myo.Handle;
            lock (lock_obj)
            {
                if (!ID_list.Contains(handle))
                {
                    ID_list.Add(handle);
                }
            }

            // add data acquisition functions based on set data stream flags
            if (emgStreamFlag)
            {
                e.Myo.EmgDataAcquired += Myo_EmgDataAcquired;
                e.Myo.SetEmgStreaming(true);
            }
            if (imuStreamFlag)
            {
                e.Myo.OrientationDataAcquired += Myo_IMUDataAcquired;
            }
            if (accStreamFlag)
            {
                e.Myo.AccelerometerDataAcquired += Myo_accDataAcquired;
            }
            if (gyroStreamFlag)
            {
                e.Myo.GyroscopeDataAcquired += Myo_gyroDataAcquired;
            }

        }

        private void Hub_MyoDisconnected(object sender, MyoEventArgs e)
        {
            // delete all data acquisition functions
            e.Myo.SetEmgStreaming(false);
            e.Myo.EmgDataAcquired -= Myo_EmgDataAcquired;
            e.Myo.OrientationDataAcquired -= Myo_IMUDataAcquired;
            e.Myo.AccelerometerDataAcquired -= Myo_accDataAcquired;
            e.Myo.GyroscopeDataAcquired -= Myo_gyroDataAcquired;

            // delete ID of Myo from the IDList
            long handle = (long)e.Myo.Handle;

            lock (lock_obj)
            {
                ID_list.Remove(handle);
            }
        }

        private void emg_thread_loop()
        {
            float curtime;
            float prevtime;
            float delay = 5f;
            sw = new Stopwatch();
            sw.Start();

            curtime = sw.Elapsed.Ticks * 1000f / Stopwatch.Frequency;
            prevtime = curtime;

            while (true)
            {
                curtime = sw.Elapsed.Ticks * 1000f / Stopwatch.Frequency;

                if (curtime - prevtime > delay)
                {
                    lock (lock_obj)
                    {
                        for (int i = 0; i < ID_list.Count; i++)
                        {
                            emg_queue_ind[i] = System.Math.Min(maxQueueLen - 1, emg_queue_ind[i] + 1);
                        }
                    }
                    prevtime = curtime;
                }
            }
        }

        private void Myo_EmgDataAcquired(object sender, EmgDataEventArgs e)
        {
            long handle = (long)e.Myo.Handle;
            int ind = ID_list.IndexOf(handle);

            // re-lock Myo to avoid vibration on double tap (only helps a bit)
            if (e.Myo.IsUnlocked)
            {
                e.Myo.Lock();
            }

            if (buzzflag)
            {
                e.Myo.Vibrate(VibrationType.Short);
                buzzflag = false;
            }

            lock (lock_obj)
            {
                // fill data array with values
                for (int i = 0; i < 8; i++)
                {
                    cliffton_emg_avg[ind, i].AddSample(System.Math.Abs(e.EmgData.GetDataForSensor(i)));
                    emg_data[ind][i].Enqueue(e.EmgData.GetDataForSensor(i));
                    emg_data[ind][i].Dequeue();
                }
                emg_queue_ind[ind] = System.Math.Max(0, emg_queue_ind[ind] - 1);
            }
        }

        private void Myo_IMUDataAcquired(object sender, OrientationDataEventArgs e)
        {
            // see Myo_EMGDataAcquired for comments, identical implementation but pulls pitch, roll, and yaw values instead of EMG channels
            long handle = (long)e.Myo.Handle;
            int ind = ID_list.IndexOf(handle);

            imu_data[ind, 0] = e.Roll;
            imu_data[ind, 1] = e.Pitch;
            imu_data[ind, 2] = e.Yaw;

        }

        private void Myo_accDataAcquired(object sender, AccelerometerDataEventArgs e)
        {
            long handle = (long)e.Myo.Handle;
            int ind = ID_list.IndexOf(handle);

            lock (lock_obj)
            {
                acc_data[ind, 0] = e.Accelerometer.X;
                acc_data[ind, 1] = e.Accelerometer.Y;
                acc_data[ind, 2] = e.Accelerometer.Z;
            }
        }

        private void Myo_gyroDataAcquired(object sender, GyroscopeDataEventArgs e)
        {
            // see Myo_EMGDataAcquired for comments, identical implementation but pulls raw gyroscope values instead of EMG channels

            long handle = (long)e.Myo.Handle;
            int ind = ID_list.IndexOf(handle);

            lock (lock_obj)
            {
                gyro_data[ind, 0] = e.Gyroscope.X;
                gyro_data[ind, 1] = e.Gyroscope.Y;
                gyro_data[ind, 2] = e.Gyroscope.Z;
            }

        }

        public void Dispose()
        {
            // delete all streaming related objects
            //channel.Dispose();
            hub.Dispose();
            t.Abort();
        }
    }
}