using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EmpireBay
{
    public class Settings
    {
        public string guideUrl { get; set; }
        public string clientUrl { get; set; }
        public string clientSha256 { get; set; }
        public string clientType { get; set; }
        public bool playEnabled { get; set; }
        public string host { get; set; }
        public int port { get; set; }
        public string clientExecutable { get; set; }
        public string connectArguments { get; set; }

        public const string Discord = "https://discord.gg/JDuRMVnJEN";
        public const string Website = "https://www.mafiarol.com/";
        public const string CommunityName = "Empire Bay Roleplay";
        public bool HasClient { get { return !String.IsNullOrWhiteSpace(clientUrl); } }

        public static bool IsHttps(string text)
        {
            Uri uri;
            return Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.UserInfo == "";
        }

        public static Settings Parse(string json)
        {
            Settings value = new JavaScriptSerializer().Deserialize<Settings>(json);
            if (value == null || !IsHttps(value.guideUrl))
                throw new InvalidDataException("Falta un enlace HTTPS para la guía.");
            Uri guide = new Uri(value.guideUrl);
            if (!(guide.Host == "discord.gg" || guide.Host == "discord.com" || guide.Host == "mafiarol.com" || guide.Host == "www.mafiarol.com"))
                throw new InvalidDataException("La guía debe estar en Discord o en MafiaRol.");
            if (value.HasClient && (!IsHttps(value.clientUrl) ||
                !Regex.IsMatch(value.clientSha256 ?? "", @"\A[0-9a-fA-F]{64}\z") ||
                !(value.clientType == "exe" || value.clientType == "msi" || value.clientType == "zip")))
                throw new InvalidDataException("Revisá el enlace, el formato y el SHA-256 del cliente.");
            if (value.playEnabled)
            {
                if (String.IsNullOrEmpty(value.host) || Uri.CheckHostName(value.host) == UriHostNameType.Unknown ||
                    !Regex.IsMatch(value.host, @"\A[a-zA-Z0-9.:-]+\z") || value.port < 1 || value.port > 65535 ||
                    !Regex.IsMatch(value.clientExecutable ?? "", @"\A[\w .-]+\.exe\z", RegexOptions.IgnoreCase) ||
                    String.IsNullOrEmpty(value.connectArguments) || !value.connectArguments.Contains("{host}") ||
                    !value.connectArguments.Contains("{port}") || value.connectArguments.IndexOfAny(new[] {'\r','\n','\0'}) >= 0)
                    throw new InvalidDataException("Faltan los datos de conexión del cliente multijugador.");
            }
            return value;
        }

        public static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        public static Settings Read()
        {
            using (var reader = new StreamReader(Resource("servidor.json")))
                return Parse(reader.ReadToEnd());
        }
    }
}
