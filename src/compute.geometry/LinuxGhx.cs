using System;
using System.Globalization;
using System.IO;
using System.Xml;
using GH_IO.Serialization;

namespace compute.geometry
{
    // VEKTORNODE: SELVA FIX — entire file. GH_IO's XML reader decodes every gh_drawing_bitmap with
    // System.Drawing.Bitmap, which throws PlatformNotSupportedException off Windows (its binary reader
    // skips the decode). Every saved .ghx carries an archive-level Thumbnail, so none would load on Linux.
    // Nothing reads the thumbnail when the "Definition" chunk is extracted, so it is dropped before parsing.
    internal static class LinuxGhx
    {
        public static string WithoutThumbnail(string xml)
        {
#if LINUX
            if (xml == null || xml.IndexOf("<chunk name=\"Thumbnail\"", StringComparison.Ordinal) < 0)
                return xml;

            var doc = new XmlDocument { PreserveWhitespace = true };
            try
            {
                doc.LoadXml(xml);
            }
            catch (XmlException)
            {
                // Not an archive: let GH_IO reject it the way callers already handle.
                return xml;
            }

            var thumbnail = doc.SelectSingleNode("/Archive/chunks/chunk[@name='Thumbnail']");
            if (thumbnail?.ParentNode is not XmlElement chunks)
                return xml;

            chunks.RemoveChild(thumbnail);
            chunks.SetAttribute("count", chunks.SelectNodes("chunk").Count.ToString(CultureInfo.InvariantCulture));
            return doc.OuterXml;
#else
            return xml;
#endif
        }

        // GH_Archive.ReadFromFile parses a .ghx itself, so its thumbnail would reach the decoder.
        public static bool ReadFromFile(GH_Archive archive, string path)
        {
#if LINUX
            if (string.Equals(Path.GetExtension(path), ".ghx", StringComparison.OrdinalIgnoreCase))
                return archive.Deserialize_Xml(WithoutThumbnail(File.ReadAllText(path)));
#endif
            return archive.ReadFromFile(path);
        }
    }
}
