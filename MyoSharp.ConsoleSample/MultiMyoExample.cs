using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MyoSharp.MultiMyo;


namespace MyoSharp.ConsoleSample
{
    internal class MultiMyoExample
    {

        #region Methods
        private static void Main()
        {
            var multimyomanager = new MultiMyoManager();
            multimyomanager.start_streaming(true, true, true, true);  // start streaming emg, imu, accel, and gyro data

            while (true)
            {

                string msg = "";
                for (int i = 0; i < multimyomanager.ID_list.Count; i++)
                {
                    msg += string.Format("MYO#{0}:", i);

                    // write emg data
                    for (int j = 0; j < 8; j++)
                    {
                        msg += string.Format("ch{0}:{1}, ", j, multimyomanager.current_emg_data[i][j]);
                    }

                    // write imu data
                    var imu_data = multimyomanager.imu_data;
                    msg += string.Format("pitch:{0:F1}, roll:{1:F1}, yaw:{2:F1}, ", imu_data[i, 0], imu_data[i, 1], imu_data[i, 2]);
                }
                Console.WriteLine(msg);
                System.Threading.Thread.Sleep(500);
            }
        }
        #endregion
    }
}