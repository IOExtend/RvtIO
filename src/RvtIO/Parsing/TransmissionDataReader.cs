using System.Text;
using System.Xml;
using OpenMcdf;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static class TransmissionDataReader
{
    public static TransmissionInfo? Read(RootStorage root)
    {
        try
        {
            using var stream = root.OpenStream("TransmissionData");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Parse(data);
        }
        catch (Exception ex) when (MalformedInput.Is(ex)) { return null; } // missing or malformed transmission data
    }

    internal static TransmissionInfo? Parse(byte[] data)
    {
        try
        {
            if (data.Length < 8) return null;

            // Format: [charCount:i32][UTF-16LE XML: charCount*2 bytes]
            int charCount = BitConverter.ToInt32(data, 0);
            if (charCount <= 0 || 4 + charCount * 2 > data.Length) return null;

            string xml = Encoding.Unicode.GetString(data, 4, charCount * 2);

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var root_el = doc.DocumentElement;
            if (root_el == null || root_el.Name != "TransmissionData") return null;

            var info = new TransmissionInfo
            {
                IsTransmitted = root_el.GetAttribute("isTransmitted") == "true",
                UserData = NullIfEmpty(root_el.GetAttribute("userData")),
            };
            if (int.TryParse(root_el.GetAttribute("version"), out int ver))
                info.Version = ver;

            foreach (XmlNode node in root_el.ChildNodes)
            {
                if (node.Name != "ExternalFileReference") continue;
                var extRef = new ExternalFileRef();
                foreach (XmlNode child in node.ChildNodes)
                {
                    switch (child.Name)
                    {
                        case "ElementId":
                            if (int.TryParse(child.InnerText, out int eid)) extRef.ElementId = eid;
                            break;
                        case "ExternalFileReferenceType": extRef.ReferenceType = child.InnerText; break;
                        case "LastSavedPath": extRef.Path = child.InnerText; break;
                        case "LastSavedAbsolutePath": extRef.AbsolutePath = child.InnerText; break;
                        case "LastSavedPathType": extRef.PathType = child.InnerText; break;
                        case "LastSavedLoadState": extRef.LoadState = child.InnerText; break;
                        case "LastSavedCentralServerLocation": extRef.ServerLocation = child.InnerText; break;
                    }
                }
                info.ExternalReferences.Add(extRef);
            }

            return info.ExternalReferences.Count > 0 ? info : null;
        }
        catch (Exception ex) when (MalformedInput.Is(ex)) { return null; } // missing or malformed transmission data
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
