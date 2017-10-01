using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scanse
{
    class LoggingSerialPort : SerialPort
    {
        private DateTime _StartTime;
        private LinkedList<string> _Log = new LinkedList<string>();

        public LoggingSerialPort(string commPort, int baudRate, Parity parity, int dataBits, StopBits stopBits)
            : base(commPort, baudRate, parity, dataBits, stopBits)
        {
            _StartTime = DateTime.UtcNow;
        }

        public new int Read(byte[] buffer, int offset, int count)
        {
            string initiatedAt = Now();
            int n = base.Read(buffer, offset, count);
            string msg = initiatedAt + " << " + count + " -> " + Now() + " " + n + ": " + StringOf(buffer, offset, n);
            System.Diagnostics.Debug.Print(msg);
            _Log.AddLast(msg);
            while (_Log.Count > 100)
                _Log.RemoveFirst();
            return n;
        }

        public new void Write(byte[] buffer, int offset, int count)
        {
            string msg = Now() + " >> " + count + ": " + StringOf(buffer, offset, count);
            System.Diagnostics.Debug.Print(msg);
            _Log.AddLast(msg);
            while (_Log.Count > 100)
                _Log.RemoveFirst();
            base.Write(buffer, offset, count);
        }

        private string Now()
        {
            return (DateTime.UtcNow - _StartTime).TotalSeconds.ToString("000.##");
        }

        private string StringOf(byte[] buffer, int offset, int count)
        {
            return new string(buffer.Skip(offset).Take(count).Select(b => (char)b).ToArray()).Replace("\n", "\\n");
        }

        public string GetEntireLog()
        {
            return _Log.Aggregate((a, b) => a + "\n" + b);
        }
    }
}
