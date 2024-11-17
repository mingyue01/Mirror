//using CSnap.Core;
//using Google.Protobuf;
//using ProtoCsmsg;
//using System;
//using System.Collections;
//using System.Collections.Generic;
//using UnityEngine;

//namespace Net
//{
//    public static class NetMsgConfig
//    {
//        #region 服务器消息
//        private static Dictionary<Type, int> MSG_ID_DICT = new Dictionary<Type, int>()
//        {
//            {typeof(CS_Login), (int)MSG_ID.MsgCsLogin},


//        };

//        private static Dictionary<int, MessageParser> MSG_PARSER_DICT = new Dictionary<int, MessageParser>()
//        {
//            {(int)MSG_ID.MsgScLogin, SC_Login.Parser},

//        };

//        public static int GetMsgId(Type type)
//        {
//            if (MSG_ID_DICT.TryGetValue(type, out var id))
//            {
//                return id;
//            }
//            CSnapLogInstance.Error("No msg id!!! {0}", type.ToString());
//            return 0;
//        }

//        public static MessageParser GetMsgParser(int msgId)
//        {
//            if (MSG_PARSER_DICT.TryGetValue(msgId, out var parser))
//            {
//                return parser;
//            }
//            CSnapLogInstance.Error("No msg parser!!! {0}", msgId.ToString());
//            return null;
//        }
//        #endregion
//    }
//}
