using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MdQuiz.Core.Protocol
{
    /// <summary>网络消息：{"t": 类型, "d": 数据}，以 UTF-8 JSON 传输。</summary>
    public sealed class Packet
    {
        [JsonProperty("t")]
        public string Type { get; set; }

        [JsonProperty("d")]
        public JToken Data { get; set; }

        public static Packet Create(string type, object data = null)
        {
            return new Packet { Type = type, Data = data == null ? null : JToken.FromObject(data, Json.Serializer) };
        }

        public T As<T>() where T : class, new()
        {
            return Data == null || Data.Type == JTokenType.Null ? new T() : Data.ToObject<T>(Json.Serializer);
        }

        public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Json.Settings));

        public static Packet FromBytes(byte[] bytes) => JsonConvert.DeserializeObject<Packet>(Encoding.UTF8.GetString(bytes), Json.Settings);

        public override string ToString() => Type;
    }

    /// <summary>统一的 JSON 设置：驼峰命名、枚举按数字、不输出 null。</summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 64,
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

        public static T Deserialize<T>(string json) => string.IsNullOrEmpty(json) ? default(T) : JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
