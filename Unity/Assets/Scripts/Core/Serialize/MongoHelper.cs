using System;
using System.ComponentModel;
using System.IO;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace ET
{
    public static class MongoHelper
    {
        [StaticField]
        private static readonly JsonWriterSettings defaultSettings = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

        // 团结当前运行时不支持 ArgumentNullException.ThrowIfNull。
        private static void EnsureNotNull(object value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
        
        public static string ToJson(object obj)
        {
            EnsureNotNull(obj, nameof(obj));
            if (obj is ISupportInitialize supportInitialize)
            {
                supportInitialize.BeginInit();
            }
            return obj.ToJson(obj.GetType(), defaultSettings);
        }

        public static string ToJson(object obj, JsonWriterSettings settings)
        {
            EnsureNotNull(obj, nameof(obj));
            if (obj is ISupportInitialize supportInitialize)
            {
                supportInitialize.BeginInit();
            }
            return obj.ToJson(obj.GetType(), settings);
        }

        public static T FromJson<T>(string str)
        {
            try
            {
                return BsonSerializer.Deserialize<T>(str);
            }
            catch (Exception e)
            {
                throw new Exception($"{str}\n{e}");
            }
        }

        public static object FromJson(Type type, string str)
        {
            return BsonSerializer.Deserialize(str, type);
        }

        public static byte[] Serialize(object obj)
        {
            EnsureNotNull(obj, nameof(obj));
            if (obj is ISupportInitialize supportInitialize)
            {
                supportInitialize.BeginInit();
            }
            // Entity transfers are read back through the abstract Entity base type and therefore
            // require the discriminator emitted by a polymorphic nominal type. Other values use
            // their concrete serializer and never fall back to ObjectSerializer's broad policy.
            Type nominalType = obj is Entity ? typeof (Entity) : obj.GetType();
            return obj.ToBson(nominalType);
        }

        public static void Serialize(object message, MemoryStream stream)
        {
            EnsureNotNull(message, nameof(message));
            if (message is ISupportInitialize supportInitialize)
            {
                supportInitialize.BeginInit();
            }

            using BsonBinaryWriter bsonWriter = new(stream, BsonBinaryWriterSettings.Defaults);
            
            BsonSerializationContext context = BsonSerializationContext.CreateRoot(bsonWriter);
            BsonSerializationArgs args = default;
            args.NominalType = message.GetType();
            IBsonSerializer serializer = BsonSerializer.LookupSerializer(args.NominalType);
            serializer.Serialize(context, args, message);
        }

        public static object Deserialize(Type type, byte[] bytes)
        {
            try
            {
                return BsonSerializer.Deserialize(bytes, type);
            }
            catch (Exception e)
            {
                throw new Exception($"from bson error: {type.FullName} {bytes.Length}", e);
            }
        }

        public static object Deserialize(Type type, byte[] bytes, int index, int count)
        {
            try
            {
                using MemoryStream memoryStream = new(bytes, index, count);
                
                return BsonSerializer.Deserialize(memoryStream, type);
            }
            catch (Exception e)
            {
                throw new Exception($"from bson error: {type.FullName} {bytes.Length} {index} {count}", e);
            }
        }

        public static object Deserialize(Type type, Stream stream)
        {
            try
            {
                return BsonSerializer.Deserialize(stream, type);
            }
            catch (Exception e)
            {
                throw new Exception($"from bson error: {type.FullName} {stream.Position} {stream.Length}", e);
            }
        }

        public static T Deserialize<T>(byte[] bytes)
        {
            try
            {
                using MemoryStream memoryStream = new(bytes);
                
                return (T)BsonSerializer.Deserialize(memoryStream, typeof (T));
            }
            catch (Exception e)
            {
                throw new Exception($"from bson error: {typeof (T).FullName} {bytes.Length}", e);
            }
        }

        public static T Deserialize<T>(byte[] bytes, int index, int count)
        {
            return (T)Deserialize(typeof (T), bytes, index, count);
        }

        public static T Clone<T>(T t)
        {
            return Deserialize<T>(Serialize(t));
        }
    }
}
