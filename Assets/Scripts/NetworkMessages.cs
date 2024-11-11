using Mirror;
using Google.Protobuf;

namespace Network
{
    public static class NetworkMessages
    {
        public static void Pack<T>(ushort protoId, T message, NetworkWriter writer) where T : IMessage
        {
            writer.WriteUShort(protoId);
            writer.Write(message.ToByteArray());
            //Msg.MessageResult.
            //writer.write
        }
    }
}
