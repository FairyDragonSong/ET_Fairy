using MongoDB.Bson;

namespace ET
{
    [EnableClass]
    public abstract class Object
    {
        public override string ToString()
        {
            // 这里不能用MongoHelper.ToJson，因为单步调试会调用ToString来显示数据
            // 如果MongoHelper.ToJson会调用BeginInit,就出大事了
            // return MongoHelper.ToJson(this);
            // Newer MongoDB drivers deliberately reject arbitrary runtime types when the nominal
            // type is object. This path is diagnostic formatting, so select this message's concrete
            // serializer instead of weakening ObjectSerializer's process-wide allow-list.
            return ((object)this).ToJson(this.GetType());
        }
        
        public string ToJson()
        {
            return MongoHelper.ToJson(this);
        }
        
        public byte[] ToBson()
        {
            return MongoHelper.Serialize(this);
        }
    }
}
