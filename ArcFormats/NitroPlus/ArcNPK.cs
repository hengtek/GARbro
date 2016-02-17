//! \file       ArcNPK.cs
//! \date       Sat Feb 06 06:07:52 2016
//! \brief      Mware resource archive.
//
// Copyright (C) 2016 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GameRes.Formats.NitroPlus
{
    internal class NpkEntry : PackedEntry
    {
        public readonly List<NpkSegment> Segments = new List<NpkSegment>();
    }

    internal class NpkSegment
    {
        public long Offset;
        public uint AlignedSize;
        public uint Size;
        public uint UnpackedSize;
    }

    internal class NpkArchive : ArcFile
    {
        public readonly Aes Encryption;

        public NpkArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, Aes enc)
            : base (arc, impl, dir)
        {
            Encryption = enc;
        }

        #region IDisposable Members
        bool _npk_disposed = false;
        protected override void Dispose (bool disposing)
        {
            if (_npk_disposed)
                return;

            if (disposing)
                Encryption.Dispose();
            _npk_disposed = true;
            base.Dispose (disposing);
        }
        #endregion
    }

    [Export(typeof(ArchiveFormat))]
    public class NpkOpener : ArchiveFormat
    {
        public override string         Tag { get { return "NPK"; } }
        public override string Description { get { return "Mware engine resource archive"; } }
        public override uint     Signature { get { return 0x324B504E; } } // 'NPK2'
        public override bool  IsHierarchic { get { return true; } }
        public override bool     CanCreate { get { return false; } }

        public static Dictionary<string, byte[]> KnownKeys = new Dictionary<string, byte[]>();

        public override ResourceScheme Scheme
        {
            get { return new Npk2Scheme { KnownKeys = KnownKeys }; }
            set { KnownKeys = ((Npk2Scheme)value).KnownKeys; }
        }

        public override ArcFile TryOpen (ArcView file)
        {
            int count = file.View.ReadInt32 (0x18);
            if (!IsSaneCount (count))
                return null;
            var key = QueryEncryption();
            if (null == key)
                return null;
            var aes = Aes.Create();
            try
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = file.View.ReadBytes (8, 0x10);
                uint index_size = file.View.ReadUInt32 (0x1C);
                using (var decryptor = aes.CreateDecryptor())
                using (var enc_index = file.CreateStream (0x20, index_size))
                using (var dec_index = new CryptoStream (enc_index, decryptor, CryptoStreamMode.Read))
                using (var index = new ArcView.Reader (dec_index))
                {
                    var dir = ReadIndex (index, count, file.MaxOffset);
                    if (null == dir)
                        return null;
                    var arc = new NpkArchive (file, this, dir, aes);
                    aes = null; // object ownership passed to NpkArchive, don't dispose
                    return arc;
                }
            }
            finally
            {
                if (aes != null)
                    aes.Dispose();
            }
        }

        List<Entry> ReadIndex (BinaryReader index, int count, long max_offset)
        {
            var name_buffer = new byte[0x80];
            var dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                index.ReadByte();
                int name_length = index.ReadUInt16();
                if (name_length > name_buffer.Length)
                    name_buffer = new byte[name_length];
                index.Read (name_buffer, 0, name_length);
                var name = Encodings.cp932.GetString (name_buffer, 0, name_length);
                var entry = FormatCatalog.Instance.Create<NpkEntry> (name);
                entry.UnpackedSize = index.ReadUInt32();
                index.Read (name_buffer, 0, 0x20); // skip
                int segment_count = index.ReadInt32();
                if (segment_count <= 0)
                    return null;
                entry.Segments.Capacity = segment_count;
                uint packed_size = 0;
                for (int j = 0; j < segment_count; ++j)
                {
                    var segment = new NpkSegment();
                    segment.Offset = index.ReadInt64();
                    segment.AlignedSize = index.ReadUInt32();
                    segment.Size = index.ReadUInt32();
                    segment.UnpackedSize = index.ReadUInt32();
                    entry.Segments.Add (segment);
                    packed_size += segment.AlignedSize;
                }
                entry.Offset = entry.Segments[0].Offset;
                entry.Size   = packed_size;
                if (!entry.CheckPlacement (max_offset))
                    return null;
                dir.Add (entry);
            }
            return dir;
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var narc = arc as NpkArchive;
            var nent = entry as NpkEntry;
            if (null == narc || null == nent)
                return base.OpenEntry (arc, entry);

            if (1 == nent.Segments.Count)
            {
                var input = narc.File.CreateStream (nent.Segments[0].Offset, nent.Segments[0].AlignedSize);
                var decryptor = narc.Encryption.CreateDecryptor();
                return new CryptoStream (input, decryptor, CryptoStreamMode.Read);
            }
            var output = new MemoryStream ((int)nent.Size);
            foreach (var segment in nent.Segments)
            {
                using (var decryptor = narc.Encryption.CreateDecryptor())
                using (var input = narc.File.CreateStream (segment.Offset, segment.AlignedSize))
                using (var dec = new CryptoStream (input, decryptor, CryptoStreamMode.Read))
                    dec.CopyTo (output);
            }
            output.Position = 0;
            return output;
        }

        byte[] QueryEncryption ()
        {
            if (0 == KnownKeys.Count)
                return null;
            return KnownKeys.Values.First();
        }
    }

    [Serializable]
    public class Npk2Scheme : ResourceScheme
    {
        public Dictionary<string, byte[]> KnownKeys;
    }
}
