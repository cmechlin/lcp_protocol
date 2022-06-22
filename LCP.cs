/*
 * 1200 Baud
 * 8 data bits
 * even parity
 * 1 stop bit
 * No flow control
 * 
 * Fields
 *      Header (Byte 1)
 *          Bits 6-8
 *              Message Type - Contains the type of req/res that is contained in the message.
 *          Bits 1-5
 *              Message Length - Contains the number of bytes following the header byte that contains this field.
 *              If this value is zero, the message has the data message format. Used to determine the message format and the number of bytes included in a message.
 *              The Message Length field provides the total number of bytes in the message, not including the checksum field.
 *      Data (Byte(s) 2-n)
 *          Data Bytes (only data message) - Contains the data associated w/ the message type. The value n is the Message Length minus 1
 *          Limited to 32 bytes max
 *          All Control/Indication
 *              0 = off
 *              1 = on
 *              The first 32-bits (4 bytes) are always sent, then 16-bits (2 bytes) for every added expansion unit, up to a max of 128 bits (16 bytes)
 *          Update 
 *              One byte, where bits 1-7 represent the Index of the indication or control being updated.
 *              The Most Significant bit, bit 8 represents the state (0-off, 1-on)
 *      Checksum (Byte n + 1)
 *          Security Field - Contains the checksum for the message. Calculated as the 8-bit result of a 2's Complement of unsigned addition of all proceeding bytes. In other words, the 
 *          sum of all the bytes of the message including the checksum should equal zero. If the sum of all the bytes in a received message is not equal to 0, then the message is discarded.
 *          
 *      LCP issues a Recall Indication message 5 seconds after power up, which the VLC responds with an All Indications message. A response timeout is indicated when the response message
 *      is not received within a configured timeout.
 *      
 *      The Station issues a Recall Control message after seeing the physical layer transition from a line break to a Line Idle state or when it detects a transmission failure. The LCP responds
 *      with an All Controls message. A Response Timeout is indicated when the response message is not received within a configured timeout.
 *      
 *      An Update message is sent when the VLC or LCP determines one of its bits have changed state. No response message is expected in return. The LCP will periodically send the Health status in
 *      an Update message, which is determined by monitoring the time since last message was received.
 *      
 *      On the hard panel, bits 1 - 16 are reserved
 *      1 - Lamp Test       (1 = lamp test on)
 *      2 - Start Switch    (1 = execute control)
 *      3-6 - Not Used
 *      7 - Local Mode      (1 = Local, 0 = Remote)
 *      8 - Remote Mode     (1 = Remote)
 *      9-15 - User Assigned
 *      16 - LCP Health     (1 = LCP is connected and healthy)
 *      17 - 128 - remaining bits
 *      
 *      Link Timeout - Maximum time to wait to complete a message
 *      Gap Adjustment - can be adjusted in 0.1 ms increments. Is 32 bit times
 *      Poll Timeout - Maximum time allowed between messages before declaring link failure
 *      Expansion Units
 *      
 *      Link Health Reporting
 *      Description                             LCP Protocol
 *      Message Transmission Failure            Parity Failure	                Physical Layer parity check failed.  See section 5.3.
 *                                              Checksum Failure                Data Link Layer checksum check failed.  See section 6.3.
 *                                              Message Timeout                 No data has been received to complete the current message.  See section 6.1.
 *                                              Incomplete Message              A new message is received before the current message is complete.  See section 6.1.
 *                                              
 *      Invalid Message Content                 Invalid Message Type            Message Type can range from 0-7.  Only 3 message types are used.  See section 7.
 *                                              Invalid Message Length          The message lengths are fixed based on the Message Type and number of configured expansion units.  See section 7.
 *                                              
 *      Link Failed                             Poll Timeout                    The LCP should be sending messages periodically.  See section 8.1.4.
 *                                              Response Timeout                Timeout occurred waiting for a response to a request.  See sections 8.1.1 and 8.1.2.
 *            
 *      
 */
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LCP
{
    public class LCP
    {
        private const int LCP_MSG_TYPE_MASK = 0xE0;
        private const int LCP_MSG_LENGTH_MASK = ~LCP_MSG_TYPE_MASK;
        private const int LCP_UPDATE_STATE_MASK = 0x80;
        private const int LCP_UPDATE_BIT_MASK = ~LCP_UPDATE_STATE_MASK;

        public enum ConnectionType
        {
            SERIAL,
            UDP,
            TCP
        }

        public enum ErrorType
        {
            SERIAL_RXOVERFLOW = 1,    // An input buffer overflow has occurred. There is either no room in the input buffer, or a character was received after the end-of-file (EOF) character.
            SERIAL_OVERRUN = 2,    // A character-buffer overrun has occurred. The next character is lost.
            SERIAL_RXPARITY = 4,    // The hardware detected a parity error.
            SERIAL_FRAME = 8,    // The hardware detected a framing error.
            SERIAL_TXFULL = 256,  // The application tried to transmit a character, but the output buffer was full.

            MSG_TIMEOUT = 16,   // No data has been received to complete the current message.  See section 6.1.
            INCOMPLETE_MSG = 32,   // A new message is received before the current message is complete.  See section 6.1.
            INVALID_MSG_TYPE = 64,    // Message Type can range from 0-7.  Only 3 message types are used.  See section 7.
            INVALID_MSG_LENGTH = 128,    // The message lengths are fixed based on the Message Type and number of configured expansion units.  See section 7.
            POLL_TIMEOUT = 512,    // The LCP should be sending messages periodically.  See section 8.1.4
            RESPONSE_TIMEOUT = 1024     // Timeout occurred waiting for a response to a request.  See sections 8.1.1 and 8.1.2.
        };

        public enum MessageType
        {
            All = 0x20,             // header for an all switch/lamp message is 001nnnnn (nnnnn bytes follow)
            Recall = 0x80,             // header for an update switch message is 111nnnnn (2 bytes follow)
            Update = 0xE0              // header for an recall message is 1000000 (0 bytes follow)
        };

        public ConnectionType Type { get; set; }
        public string Status { get; private set; }

        private SerialPort _sp;
        private UdpClient _udp;
        private TcpClient _tcp;
        private IPEndPoint _ipEndPoint;

        #region Constructor & Deconstructor
        public LCP()
        {

        }

        public LCP(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
        {
            Type = ConnectionType.SERIAL;
            _sp = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
        }

        public LCP(string ip, int port)
        {
            _ipEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);

            switch (Type)
            {
                case ConnectionType.UDP:
                    try
                    {
                        _udp = new UdpClient(_ipEndPoint);

                        while (true)
                        {
                            Console.WriteLine("Waiting for broadcast");
                            byte[] bytes = _udp.Receive(ref _ipEndPoint);

                            Console.WriteLine($"Received broadcast from {_ipEndPoint} :");
                            Console.WriteLine($" {Encoding.ASCII.GetString(bytes, 0, bytes.Length)}");
                        }
                    }
                    catch (SocketException e)
                    {
                        Console.WriteLine(e);
                    }
                    finally
                    {
                        _udp.Close();
                    }
                    break;
                case ConnectionType.TCP:
                    try
                    {
                        _tcp = new TcpClient(_ipEndPoint);

                        NetworkStream ns = _tcp.GetStream();

                        byte[] bytes = new byte[1024];
                        int bytesRead = ns.Read(bytes, 0, bytes.Length);

                        Console.WriteLine(Encoding.ASCII.GetString(bytes, 0, bytesRead));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.ToString());
                    }
                    finally
                    {
                        _tcp.Close();
                    }
                    break;
                default:
                    break;
            }
        }

        ~LCP()
        {
            Console.WriteLine();
        }
        #endregion

        public bool Open()
        {
            switch (Type)
            {
                case ConnectionType.SERIAL:
                    if (!_sp.IsOpen)
                    {
                        try
                        {
                            _sp.Open();
                        }
                        catch (Exception err)
                        {
                            Status = "Error opening " + _sp.PortName + ": " + err.Message;
                            return false;
                        }
                        Status = _sp.PortName + " opened successfully";
                        return true;
                    }
                    else
                    {
                        Status = _sp.PortName + " already opened";
                        return false;
                    }
                case ConnectionType.UDP:
                    if (!_udp.Client.Connected)
                    {
                        try
                        {
                            _udp.Connect(_ipEndPoint);
                        }
                        catch (Exception err)
                        {
                            Status = "Error opening " + _udp.Client.RemoteEndPoint + ":" + err.Message;
                            return false;
                        }
                        Status = _udp.Client.RemoteEndPoint + " opened successfully";
                        return true;
                    }
                    else
                    {
                        Status = _udp.Client.RemoteEndPoint + " already opened";
                        return false;
                    }
                case ConnectionType.TCP:
                    if (!_sp.IsOpen)
                    {
                        try
                        {
                            _tcp.Connect(_ipEndPoint);
                        }
                        catch (Exception err)
                        {
                            Status = "Error opening " + _tcp.Client.RemoteEndPoint + ": " + err.Message;
                            return false;
                        }
                        Status = _tcp.Client.RemoteEndPoint + " opened successfully";
                        return true;
                    }
                    else
                    {
                        Status = _tcp.Client.RemoteEndPoint + " already opened";
                        return false;
                    }
                default:
                    Status = "Open Failed: Unknown connection type: " + Type;
                    return false;
            }
        }

        public bool Open(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
        {
            if (!_sp.IsOpen)
            {
                _sp.PortName = portName;
                _sp.BaudRate = baudRate;
                _sp.DataBits = dataBits;
                _sp.Parity = parity;
                _sp.StopBits = stopBits;

                _sp.ReadTimeout = 1000;
                _sp.WriteTimeout = 1000;

                try
                {
                    _sp.Open();
                }
                catch (Exception e)
                {
                    Status = "Error opening " + portName + ": " + e.Message;
                    return false;
                }
                Status = portName + " opened successfully";
                return true;
            }
            else
            {
                Status = portName + " already opened";
                return false;
            }
        }

        public bool Close()
        {
            switch (Type)
            {
                case ConnectionType.SERIAL:
                    if (_sp.IsOpen)
                    {
                        try
                        {
                            _sp.Close();
                        }
                        catch (Exception err)
                        {
                            Status = "Error closing " + _sp.PortName + ": " + err.Message;
                            return false;
                        }
                        Status = _sp.PortName + " closed successfully";
                        return true;
                    }
                    else
                    {
                        Status = _sp.PortName + " is not open";
                        return false;
                    }
                case ConnectionType.UDP:
                    if (_udp.Client.Connected)
                    {
                        try
                        {
                            _udp.Close();
                        }
                        catch (Exception err)
                        {
                            Status = "Error closing UDP connection: " + err.Message;
                            return false;
                            throw;
                        }
                        Status = "UDP closed successfully";
                        return true;
                    }
                    else
                    {
                        Status = "UDP connection is not open";
                        return false;
                    }
                case ConnectionType.TCP:
                    if (_tcp.Connected)
                    {
                        try
                        {
                            _tcp.Close();
                        }
                        catch (Exception err)
                        {
                            Status = "Error closing TCP connection: " + err.Message;
                            return false;
                            throw;
                        }
                        Status = "TCP closed successfully";
                        return true;
                    }
                    else
                    {
                        Status = "TCP connection is not open";
                        return false;
                    }
                default:
                    Status = "Close failed: Unknown connection type: " + Type;
                    return false;
            }

        }

        public bool Send(byte[] msg)
        {
            switch (Type)
            {
                case ConnectionType.SERIAL:
                    if (_sp.IsOpen)
                    {
                        try
                        {
                            _sp.Write(msg, 0, msg.Length);
                            return true;
                        }
                        catch (Exception err)
                        {
                            Status = "Serial TX Error: " + err.Message;
                            return false;
                        }
                    }
                    else
                    {
                        Status = "Serial port [" + _sp.PortName + "] not open";
                        return false;
                    }
                case ConnectionType.UDP:
                    if (_udp.Client.Connected)
                    {

                        return true;
                    }
                    else
                    {
                        Status = "UDP connection not open";
                        return false;
                    }
                case ConnectionType.TCP:
                    if (_tcp.Connected)
                    {
                        return true;
                    }
                    else
                    {
                        Status = "TCP connection not open";
                        return false;
                    }
                default:
                    Status = "Send Failed: Unknown connection type: " + Type;
                    return false;
            }
        }

        public void Receive(byte[] data)
        {

        }

        private byte[] BuildMessage(byte type, byte[] data)
        {
            byte[] msg = new byte[2 + data.Length];
            // Recall is 1 byte
            // Update is 2 bytes
            // All is
            switch ((MessageType)type)
            {
                case MessageType.All:
                    msg[0] = (int)MessageType.All | 17;
                    for (int i = 0; i < data.Length; i++)
                    {
                        msg[i + 1] = data[i];
                    }
                    msg[msg.Length - 1] = checksum(msg);
                    break;
                case MessageType.Update:
                    msg[0] = (int)MessageType.Update | 2;
                    msg[1] = data[0];
                    msg[2] = checksum(msg);
                    break;
                case MessageType.Recall:

                    break;
                default:
                    break;
            }

            return msg;
        }

        public byte checksum(byte[] msg)
        {
            //int length = msg[0] & LCP_MSG_LENGTH_MASK;            
            int chksum = 0;

            for (int i = 0; i < msg.Length; i++)
            {
                chksum += msg[i];
            }
            chksum = (~chksum & 0xff) + 1;

            return (byte)chksum;
        }
    }

    public class LCPMessage
    {
        public int header { get; set; }

        public int type { get; set; }

        public int length { get; set; }
        public byte[] data { get; set; }
        public byte checksum { get; set; }

        public LCPMessage(byte[] raw)
        {

        }
    }
}