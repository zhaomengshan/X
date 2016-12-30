﻿using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NewLife.Data;
using NewLife.Messaging;

namespace NewLife.Net
{
    /// <summary>粘包处理接口</summary>
    public interface IPacket
    {
        /// <summary>创建消息</summary>
        /// <returns></returns>
        IMessage CreateMessage();

        /// <summary>加入请求队列</summary>
        /// <param name="request">请求的数据</param>
        /// <param name="remote">远程</param>
        /// <param name="msTimeout">超时取消时间</param>
        Task<Packet> Add(Packet request, IPEndPoint remote, Int32 msTimeout);

        /// <summary>检查请求队列是否有匹配该响应的请求</summary>
        /// <param name="response">响应的数据</param>
        /// <param name="remote">远程</param>
        /// <returns></returns>
        Boolean Match(Packet response, IPEndPoint remote);

        /// <summary>分析数据流，得到一帧数据</summary>
        /// <param name="pk"></param>
        /// <returns></returns>
        Packet Parse(Packet pk);
    }

    /// <summary>粘包处理接口工厂</summary>
    public interface IPacketFactory
    {
        /// <summary>创建粘包处理实例，内含缓冲区，不同会话不能共用</summary>
        /// <returns></returns>
        IPacket Create();
    }

    /// <summary>头部指明长度的封包格式</summary>
    public class HeaderLengthPacket : IPacket
    {
        #region 属性
        /// <summary>长度所在位置，默认2</summary>
        public Int32 Offset { get; set; } = 2;

        /// <summary>长度占据字节数，1/2/4个字节，0表示压缩编码整数，默认2</summary>
        public Int32 Size { get; set; } = 2;

        /// <summary>过期时间，超过该时间后按废弃数据处理，默认500ms</summary>
        public Int32 Expire { get; set; } = 500;

        private DateTime _last;
        #endregion

        /// <summary>创建消息</summary>
        /// <returns></returns>
        public virtual IMessage CreateMessage() { return null; }

        #region 匹配队列
        /// <summary>加入请求队列</summary>
        /// <param name="request">请求的数据</param>
        /// <param name="remote">远程</param>
        /// <param name="msTimeout">超时取消时间</param>
        public virtual Task<Packet> Add(Packet request, IPEndPoint remote, Int32 msTimeout) { return null; }

        /// <summary>检查请求队列是否有匹配该响应的请求</summary>
        /// <param name="response">响应的数据</param>
        /// <param name="remote">远程</param>
        /// <returns></returns>
        public virtual Boolean Match(Packet response, IPEndPoint remote) { return true; }
        #endregion

        #region 粘包处理
        /// <summary>内部缓存</summary>
        private MemoryStream _ms;

        /// <summary>分析数据流，得到一帧数据</summary>
        /// <param name="pk"></param>
        /// <returns></returns>
        public Packet Parse(Packet pk)
        {
            var nodata = _ms == null || _ms.Position < 0 || _ms.Position >= _ms.Length;

            // 内部缓存没有数据，直接判断输入数据流是否刚好一帧数据，快速处理，绝大多数是这种场景
            if (nodata)
            {
                if (pk == null) return null;

                var len = GetLength(pk.GetStream());
                if (len > 0 && len == pk.Count) return pk;
            }

            if (_ms == null) _ms = new MemoryStream();

            // 加锁，避免多线程冲突
            lock (_ms)
            {
                if (pk != null)
                {
                    // 超过该时间后按废弃数据处理
                    var now = DateTime.Now;
                    if (_last.AddMilliseconds(Expire) < now)
                    {
                        _ms.SetLength(0);
                        _ms.Position = 0;
                    }
                    _last = now;

                    // 拷贝数据到最后面
                    var p = _ms.Position;
                    _ms.Position = _ms.Length;
                    _ms.Write(pk.Data, pk.Offset, pk.Count);
                    _ms.Position = p;
                }

                var len = GetLength(_ms);
                if (len <= 0) return null;

                // 长度足够，返回数据帧
                //var rs = Sub(_ms, len);
                //_ms.Seek(len, SeekOrigin.Current);
                //return rs;

                // 拷贝比较容易处理，反正粘包的可能性不是很高
                return new Packet(_ms.ReadBytes(len));
            }
        }

        /// <summary>从数据流中获取整帧数据长度</summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        protected virtual Int32 GetLength(Stream stream)
        {
            var p = stream.Position;
            // 数据不够，连长度都读取不了
            if (p + Offset >= stream.Length) return 0;

            // 移动到长度所在位置
            if (Offset > 0) stream.Seek(Offset, SeekOrigin.Current);

            // 读取大小
            var len = 0;
            switch (Size)
            {
                case 0:
                    len = stream.ReadEncodedInt();
                    break;
                case 1:
                    len = stream.ReadByte();
                    break;
                case 2:
                    len = stream.ReadBytes(2).ToInt();
                    break;
                case 4:
                    len = (Int32)stream.ReadBytes(4).ToUInt32();
                    break;
                default:
                    throw new NotSupportedException();
            }

            // 判断后续数据是否足够
            if (stream.Position + len > stream.Length)
            {
                // 长度不足，恢复位置
                stream.Position = p;
                return 0;
            }

            // 数据长度加上头部长度
            len += (Int32)(stream.Position - p);

            // 恢复位置
            stream.Position = p;

            return len;
        }
        #endregion

#if DEBUG
        /// <summary>粘包测试</summary>
        public static void Test()
        {
            var svr = new NetServer();
            svr.Port = 777;
            svr.SessionPacket = new HeaderLengthPacketFactory();
            svr.Log = Log.XTrace.Log;
            svr.LogReceive = true;
            svr.Start();

            // 凑齐10个带有长度的数据帧一起发出
            var ms = new MemoryStream();
            for (int i = 0; i < 5; i++)
            {
                var size = i < 4 ? Security.Rand.Next(1400) : Security.Rand.Next(2000, 30000);
                var str = Security.Rand.NextString(size);
                var s = str.Substring(0, Math.Min(str.Length, 16));
                //var h = str.GetBytes().ToHex();
                var mm = new MemoryStream();
                mm.WriteArray(str.GetBytes());
                var h = mm.ToArray().ToHex();
                h = h.Substring(0, Math.Min(h.Length, 32));
                Console.WriteLine("{0}\t{1}\t{2}", mm.ToArray().Length, s, h);

                ms.WriteArray(str.GetBytes());
            }

            var client = new NetUri("tcp://127.0.0.1:777").CreateRemote();
            //client.Remote.Address = NetHelper.MyIP();
            //client.Remote.Address = System.Net.IPAddress.Parse("1.0.0.13");
            client.Log = Log.XTrace.Log;
            client.LogSend = true;
            //client.BufferSize = 1500;
            client.SendAsync(ms.ToArray());

            Console.ReadKey(true);

            client.Close("结束");
            svr.Dispose();
        }
#endif
    }

    /// <summary>头部长度粘包处理工厂</summary>
    public class HeaderLengthPacketFactory : IPacketFactory
    {
        #region 属性
        /// <summary>长度所在位置，默认2</summary>
        public Int32 Offset { get; set; } = 2;

        /// <summary>长度占据字节数，1/2/4个字节，0表示压缩编码整数，默认2</summary>
        public Int32 Size { get; set; } = 2;

        /// <summary>过期时间，超过该时间后按废弃数据处理，默认500ms</summary>
        public Int32 Expire { get; set; } = 500;
        #endregion

        /// <summary>创建粘包处理实例，内含缓冲区，不同会话不能共用</summary>
        /// <returns></returns>
        public virtual IPacket Create()
        {
            return new HeaderLengthPacket
            {
                Offset = Offset,
                Size = Size,
                Expire = Expire
            };
        }
    }
}