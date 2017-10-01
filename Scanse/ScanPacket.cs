using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scanse
{
    /// <summary>
    /// Class representing the data block returned from the Sweep.
    /// </summary>
    public class ScanPacket
    {
        const float SCALING_FACTOR = 16.0f;

        bool _bIsSync;        // 1 -> first reading of new scan, 0 otherwise
        UInt16 _rawAngle;     // fixed point value: (degrees * 16)
        UInt16 _distance;     // cm
        byte _signalStrength; // 0:255, higher is better

        public ScanPacket(bool bIsSync, UInt16 rawAngle, UInt16 distance, byte signalStrength)
        {
            _bIsSync = bIsSync;
            _rawAngle = rawAngle;
            _distance = distance;
            _signalStrength = signalStrength;
        }

        public bool IsSync()
        {
            return _bIsSync;
        }

        public float GetAngleDegrees()
        {
            return _rawAngle / SCALING_FACTOR;
        }

        public UInt16 GetAngleRaw()
        {
            return _rawAngle;
        }

        public UInt16 GetDistanceCentimeters()
        {
            return _distance;
        }

        public byte GetSignalStrength()
        {
            return _signalStrength;
        }

        public float GetNormalizedSignalStrength()
        {
            return _signalStrength / 255;
        }
    }
}
