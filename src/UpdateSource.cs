using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Screenline
{
    // A deployment opts in with a public key and one exact HTTPS origin/path.
    // No private key or cloud credential is read by the application.
    internal sealed class UpdateSource
    {
        private const int MaximumConfigBytes = 32 * 1024;
        private readonly RSAParameters publicKey;
        internal Uri BaseUri { get; private set; }

        private UpdateSource(Uri baseUri, RSAParameters publicKey)
        {
            BaseUri = baseUri;
            this.publicKey = publicKey;
        }

        internal static UpdateSource LoadOptional(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "update-source.json");
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumConfigBytes) return null;
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        return FromConfigJson(reader.ReadToEnd());
                }
            }
            catch (UpdateException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (DecoderFallbackException) { return null; }
        }

        internal static UpdateSource FromConfigJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            Dictionary<string, string> fields = ReadFlatObject(json, MaximumConfigBytes,
                new string[] { "BaseUrl", "PublicKeyXml" }, new string[] { "string", "string" }, false);
            string baseUrl, keyXml;
            fields.TryGetValue("BaseUrl", out baseUrl);
            fields.TryGetValue("PublicKeyXml", out keyXml);
            if (String.IsNullOrWhiteSpace(baseUrl) && String.IsNullOrWhiteSpace(keyXml)) return null;
            if (String.IsNullOrWhiteSpace(baseUrl) || String.IsNullOrWhiteSpace(keyXml))
                throw new UpdateException("国内更新源需要同时配置 HTTPS 地址和签名公钥。");
            Uri baseUri;
            if (baseUrl.Length > 2048 || baseUrl != baseUrl.Trim() || !baseUrl.EndsWith("/", StringComparison.Ordinal) ||
                baseUrl.IndexOf('\\') >= 0 || baseUrl.IndexOf('%') >= 0 ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) || !IsHttps(baseUri) ||
                !String.IsNullOrEmpty(baseUri.Query) || baseUri.AbsolutePath.IndexOf('%') >= 0)
                throw new UpdateException("国内更新源地址必须是以 / 结尾的 HTTPS 目录。");
            string[] segments = baseUrl.Substring(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3).Split('/');
            for (int i = 1; i < segments.Length; i++)
                if (segments[i] == "." || segments[i] == "..")
                    throw new UpdateException("国内更新源地址不能包含相对目录。");
            return new UpdateSource(baseUri, ParsePublicKey(keyXml));
        }

        internal bool VerifySignature(byte[] payload, byte[] signature)
        {
            if (payload == null || signature == null || signature.Length != publicKey.Modulus.Length) return false;
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                return rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature);
            }
        }

        internal Uri LatestUri { get { return new Uri(BaseUri, "stable/latest.json"); } }
        internal Uri InstallerUri(string tag, string installer) { return new Uri(BaseUri, "releases/" + tag + "/" + installer); }

        internal static bool IsAllowedUri(Uri uri, Uri baseUri)
        {
            if (!IsHttps(uri) || !IsHttps(baseUri) || !String.IsNullOrEmpty(uri.Query) ||
                !String.Equals(uri.DnsSafeHost, baseUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.IndexOf('%') >= 0 || uri.AbsolutePath.IndexOf('\\') >= 0) return false;
            return uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal);
        }

        internal static bool SameUri(Uri left, Uri right)
        {
            return left != null && right != null && String.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
        }

        private static bool IsHttps(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
                String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment);
        }

        private static RSAParameters ParsePublicKey(string xml)
        {
            try
            {
                if (xml.Length > 16 * 1024) throw new UpdateException("更新签名公钥无效。");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 };
                var document = new XmlDocument { XmlResolver = null };
                using (var text = new StringReader(xml))
                using (var reader = XmlReader.Create(text, settings)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "RSAKeyValue" || root.Attributes.Count != 0)
                    throw new UpdateException("更新签名公钥无效。");
                byte[] modulus = null, exponent = null;
                foreach (XmlNode node in root.ChildNodes)
                {
                    XmlElement element = node as XmlElement;
                    if (element == null || element.Attributes.Count != 0 || element.ChildNodes.Count != 1 ||
                        element.FirstChild.NodeType != XmlNodeType.Text) throw new UpdateException("更新配置只能包含 RSA 公钥。");
                    if (element.Name == "Modulus" && modulus == null) modulus = Convert.FromBase64String(element.InnerText);
                    else if (element.Name == "Exponent" && exponent == null) exponent = Convert.FromBase64String(element.InnerText);
                    else throw new UpdateException("更新配置只能包含 RSA 公钥。");
                }
                if (modulus == null || exponent == null || modulus.Length < 256 || modulus.Length > 1024 ||
                    exponent.Length == 0 || exponent.Length > 8) throw new UpdateException("更新签名需要至少 2048 位 RSA 公钥。");
                var parameters = new RSAParameters { Modulus = modulus, Exponent = exponent };
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.ImportParameters(parameters);
                    if (rsa.KeySize < 2048) throw new UpdateException("更新签名需要至少 2048 位 RSA 公钥。");
                    return rsa.ExportParameters(false);
                }
            }
            catch (UpdateException) { throw; }
            catch (Exception ex)
            {
                if (!(ex is XmlException) && !(ex is FormatException) && !(ex is CryptographicException) && !(ex is ArgumentException)) throw;
                throw new UpdateException("更新签名公钥无法读取。");
            }
        }

        // JSON is deliberately flat and exact: duplicate, unknown or nested members
        // must not be interpreted differently by the signer and the client.
        internal static Dictionary<string, string> ReadFlatObject(string json, int maximumBytes,
            string[] names, string[] types, bool requireAll)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > maximumBytes)
                throw new UpdateException("更新信息大小无效。");
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(json.TrimStart('\uFEFF'));
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 4, MaxStringContentLength = maximumBytes, MaxArrayLength = maximumBytes };
                var document = new XmlDocument { XmlResolver = null };
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "root" || root.GetAttribute("type") != "object" || root.Attributes.Count != 1)
                    throw new UpdateException("更新信息格式无效。");
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (XmlNode node in root.ChildNodes)
                {
                    XmlElement element = node as XmlElement;
                    int index = element == null ? -1 : Array.IndexOf(names, element.Name);
                    if (index < 0 || result.ContainsKey(element.Name) || element.GetAttribute("type") != types[index] || element.Attributes.Count != 1)
                        throw new UpdateException("更新信息包含不受支持或重复的字段。");
                    foreach (XmlNode child in element.ChildNodes)
                        if (child.NodeType != XmlNodeType.Text && child.NodeType != XmlNodeType.Whitespace && child.NodeType != XmlNodeType.SignificantWhitespace)
                            throw new UpdateException("更新信息包含嵌套字段。");
                    result.Add(element.Name, element.InnerText);
                }
                if (requireAll && result.Count != names.Length) throw new UpdateException("更新信息缺少必要字段。");
                return result;
            }
            catch (UpdateException) { throw; }
            catch (Exception ex)
            {
                if (!(ex is SerializationException) && !(ex is XmlException) && !(ex is ArgumentException) && !(ex is InvalidOperationException)) throw;
                throw new UpdateException("更新信息无法读取。");
            }
        }
    }
}
