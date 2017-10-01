using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Scanse
{
    public class Sweep : IDisposable
    {
        private LoggingSerialPort _Serial;

        bool bIsScanning;

        // Command Prefixes (See Sweep User Manual for CommProtocol)
        readonly byte _COMMAND_TERMINATION = (byte)'\n';
        readonly byte[] _DATA_ACQUISITION_START = { (byte)'D', (byte)'S' };
        readonly byte[] _DATA_ACQUISITION_STOP = { (byte)'D', (byte)'X' };
        readonly byte[] _MOTOR_READY = { (byte)'M', (byte)'Z' };
        readonly byte[] _MOTOR_SPEED_ADJUST = { (byte)'M', (byte)'S' };
        readonly byte[] _MOTOR_INFORMATION = { (byte)'M', (byte)'I' };
        readonly byte[] _SAMPLE_RATE_ADJUST = { (byte)'L', (byte)'R' };
        readonly byte[] _SAMPLE_RATE_INFORMATION = { (byte)'L', (byte)'I' };
        readonly byte[] _VERSION_INFORMATION = { (byte)'I', (byte)'V' };
        readonly byte[] _DEVICE_INFORMATION = { (byte)'I', (byte)'D' };
        readonly byte[] _RESET_DEVICE = { (byte)'R', (byte)'R' };

        // Sync/Error char bit masks
        const byte _E6_MASK = 0x80;
        const byte _E5_MASK = 0x40;
        const byte _E4_MASK = 0x20;
        const byte _E3_MASK = 0x10;
        const byte _E2_MASK = 0x08;
        const byte _E1_MASK = 0x04;
        const byte _E0_MASK = 0x02;
        const byte _SYNC_MASK = 0x01;

        // Arrays to hold responses
        byte[] _responseHeader = new byte[6];
        byte[] _responseParam = new byte[9];
        byte[] _responseScanPacket = new byte[7];
        byte[] _responseInfoDevice = new byte[18];
        byte[] _responseInfoVersion = new byte[21];
        byte[] _responseInfoSetting = new byte[5];

        // Available Motor Speed Codes for the setMotorSpeed method
        static readonly Dictionary<MotorSpeed, byte[]> MOTOR_SPEED_CODES = new Dictionary<MotorSpeed, byte[]>()
        {
            { MotorSpeed.Speed0Hz , new byte[] { (byte)'0', (byte)'0' } },
            { MotorSpeed.Speed1Hz , new byte[] { (byte)'0', (byte)'1' } },
            { MotorSpeed.Speed2Hz , new byte[] { (byte)'0', (byte)'2' } },
            { MotorSpeed.Speed3Hz , new byte[] { (byte)'0', (byte)'3' } },
            { MotorSpeed.Speed4Hz , new byte[] { (byte)'0', (byte)'4' } },
            { MotorSpeed.Speed5Hz , new byte[] { (byte)'0', (byte)'5' } },
            { MotorSpeed.Speed6Hz , new byte[] { (byte)'0', (byte)'6' } },
            { MotorSpeed.Speed7Hz , new byte[] { (byte)'0', (byte)'7' } },
            { MotorSpeed.Speed8Hz , new byte[] { (byte)'0', (byte)'8' } },
            { MotorSpeed.Speed9Hz , new byte[] { (byte)'0', (byte)'9' } },
            { MotorSpeed.Speed10Hz , new byte[] { (byte)'1', (byte)'0' } },
        };

        // Available Sample Rate Codes for the setSampleRate method
        static readonly Dictionary<SampleRate, byte[]> SAMPLE_RATE_CODES = new Dictionary<SampleRate, byte[]>()
        {
            { SampleRate.Rate500Hz, new byte[] { (byte)'0', (byte)'1' } },
            { SampleRate.Rate750Hz, new byte[] { (byte)'0', (byte)'2' } },
            { SampleRate.Rate1000Hz, new byte[] { (byte)'0', (byte)'3' } },
        };

        public Sweep(string commPort)
        {
            _Serial = new LoggingSerialPort(commPort, 115200, Parity.None, 8, StopBits.One);
            _Serial.DtrEnable = false;
            _Serial.Open();
        }

        public void Dispose()
        {
            if (_Serial != null && _Serial.IsOpen)
                _Serial.Close();
        }

        public bool IsScanning()
        {
            return bIsScanning;
        }

        public bool StartScanning()
        {
            if (bIsScanning)
                return false;

            // wait until the device is ready (calibration complete + motor stabilized)
            if (!WaitUntilMotorReady())
                return false;

            _writeCommand(_DATA_ACQUISITION_START);
            // wait for the receipt (possible timeout)
            if (_readResponseHeader())
            {
                // TODO: validate receipt
                bIsScanning = true;
                return true;
            }
            return false;
        }

        public bool StopScanning()
        {
            _writeCommand(_DATA_ACQUISITION_STOP);

            // wait for the device to stop sending packets
            Thread.Sleep(500);

            // then flush the buffer and send STOP again to check for a receipt
            _flushInputBuffer();
            _writeCommand(_DATA_ACQUISITION_STOP);

            // wait for the receipt (possible timeout)
            if (_readResponseHeader())
            {
                // TODO: validate receipt
                bIsScanning = false;
                return true;
            }
            return false;
        }

        public ScanPacket GetReading()
        {
            if (!bIsScanning)
            {
                return null;
            }

            // wait for the receipt (possible timeout)
            if (_readResponseScanPacket())
            {
                // TODO: validate receipt
                byte i = 0;

                bool bIsSync = (_responseScanPacket[i++] & _SYNC_MASK) != 0;

                // read raw fixed point azimuth value
                UInt16 rawAngle_lsb = _responseScanPacket[i++];
                UInt16 rawAngle_msb = (UInt16)(_responseScanPacket[i++] << 8);
                UInt16 rawAngle = (UInt16)(rawAngle_lsb + rawAngle_msb);

                // read distance value
                UInt16 distance_lsb = _responseScanPacket[i++];
                UInt16 distance_msb = (UInt16)(_responseScanPacket[i++] << 8);
                UInt16 distance = (UInt16)(distance_lsb + distance_msb);

                // read signal strength value
                byte signalStrength = _responseScanPacket[i++];

                return new ScanPacket(bIsSync, rawAngle, distance, signalStrength);
            }

            return null;
        }

        bool GetMotorReady()
        {
            if (bIsScanning)
                return false;
            _writeCommand(_MOTOR_READY);
            if (_readResponseInfoSetting())
            {
                // TODO: validate receipt (hold off until performance hit is determined)
                var readyCode = new byte[] { _responseInfoSetting[2], _responseInfoSetting[3] };
                // readyCode == 0 indicates device is ready
                return _ascii_bytes_to_integer(readyCode) == 0;
            }
            return false;
        }

        bool WaitUntilMotorReady()
        {
            if (bIsScanning)
                return false;
            // only check for 10 seconds (20 iterations with 500ms pause)
            for (byte i = 0; i < 20; ++i)
            {
                if (GetMotorReady())
                    return true;
                Thread.Sleep(500);
            }
            // timeout after 10 seconds
            return false;
        }

        public int GetMotorSpeed()
        {
            if (bIsScanning)
                return 0;

            _writeCommand(_MOTOR_INFORMATION);
            if (_readResponseInfoSetting())
            {
                // TODO: validate receipt (hold off until performance hit is determined)
                var speedCode = new byte[] { _responseInfoSetting[2], _responseInfoSetting[3] };
                return _ascii_bytes_to_integer(speedCode);
            }
            return -1;
        }

        public bool SetMotorSpeed(MotorSpeed speed)
        {
            if (bIsScanning)
                return false;

            // wait until the device is ready (calibration complete + motor stabilized)
            if (!WaitUntilMotorReady())
                return false;

            _writeCommandWithArgument(_MOTOR_SPEED_ADJUST, MOTOR_SPEED_CODES[speed]);
            // wait for the receipt (possible timeout)
            if (_readResponseParam())
            {
                // TODO: validate receipt
                return true;
            }
            return false;
        }

        public int GetSampleRate()
        {
            if (bIsScanning)
                return 0;

            _writeCommand(_SAMPLE_RATE_INFORMATION);
            if (_readResponseInfoSetting())
            {
                // TODO: validate receipt (hold off until performance hit is determined)
                var sampleRateCode = new byte[] { _responseInfoSetting[2], _responseInfoSetting[3] };
                switch (_ascii_bytes_to_integer(sampleRateCode))
                {
                    case 1:
                        return 500;
                    case 2:
                        return 750;
                    case 3:
                        return 1000;
                    default:
                        break;
                }
            }
            return -1;
        }

        public bool SetSampleRate(SampleRate rate)
        {
            if (bIsScanning)
                return false;

            _writeCommandWithArgument(_SAMPLE_RATE_ADJUST, SAMPLE_RATE_CODES[rate]);
            // wait for the receipt (possible timeout)
            if (_readResponseParam())
            {
                // TODO: validate receipt
                return true;
            }
            return false;
        }

        public void Reset()
        {
            _writeCommand(_RESET_DEVICE);
        }

        void _writeCommand(byte[] cmd)
        {
            var command = new byte[] { cmd[0], cmd[1], _COMMAND_TERMINATION };

            _Serial.Write(command, 0, 3);
        }

        void _writeCommandWithArgument(byte[] cmd, byte[] arg)
        {
            var command = new byte[] { cmd[0], cmd[1], arg[0], arg[1], _COMMAND_TERMINATION };

            _Serial.Write(command, 0, 5);
        }

        bool _readResponseHeader()
        {
            return _read(_responseHeader);
        }

        bool _readResponseParam()
        {
            return _read(_responseParam);
        }

        bool _readResponseScanPacket()
        {
            return _read(_responseScanPacket);
        }

        bool _readResponseInfoDevice()
        {
            return _read(_responseInfoDevice);
        }

        bool _readResponseInfoVersion()
        {
            return _read(_responseInfoVersion);
        }

        bool _readResponseInfoSetting()
        {
            return _read(_responseInfoSetting);
        }

        bool _read(byte[] buffer)
        {
            // determine the expected number of bytes to read
            int len = buffer.Length;

            // set a timeout on the read
            _Serial.ReadTimeout = 1000;

            // attempt to read (can timeout)
            int i = 0;
            while (i < len)
            {
                try
                {
                    int n = _Serial.Read(buffer, i, len - i);
                    i += n;
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            // return true if the expected num of bytes were read
            return i == len;
        }

        void _flushInputBuffer()
        {
            while (_Serial.BytesToRead > 0)
            {
                _Serial.ReadExisting();
            }
        }

        int _ascii_bytes_to_integer(byte[] bytes)
        {
            const byte ASCIINumberBlockOffset = 48;

            byte num1 = (byte)(bytes[0] - ASCIINumberBlockOffset);
            byte num2 = (byte)(bytes[1] - ASCIINumberBlockOffset);

            if (num1 > 9 || num2 > 9 || num1 < 0 || num2 < 0)
                return -1;

            return (num1 * 10) + (num2 * 1);
        }
    }
}
