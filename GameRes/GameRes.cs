//! \file       GameRes.cs
//! \date       Mon Jun 30 20:12:13 2014
//! \brief      game resources browser.
//
// Copyright (C) 2014-2015 by morkt
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
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using GameRes.Collections;
using GameRes.Strings;

namespace GameRes
{
    /// <summary>
    /// Basic filesystem entry.
    /// </summary>
    public class Entry
    {
        public virtual string Name   { get; set; }
        public virtual string Type   { get; set; }
        public         long   Offset { get; set; }
        public         uint   Size   { get; set; }

        public Entry ()
        {
            Type = "";
            Offset = -1;
        }

        /// <summary>
        /// Check whether entry lies within specified file bound.
        /// </summary>
        public bool CheckPlacement (long max_offset)
        {
            return Offset < max_offset && Size < max_offset && Offset <= max_offset - Size;
        }
    }

    public class PackedEntry : Entry
    {
        public uint UnpackedSize { get; set; }
        public bool IsPacked     { get; set; }
    }

    public abstract class IResource
    {
        /// <summary>Short tag sticked to resource (usually filename extension)</summary>
        public abstract string Tag { get; }

        /// <summary>Resource description (its source/inventor)</summary>
        public abstract string Description { get; }

        /// <summary>Resource type (image/archive/script)</summary>
        public abstract string Type { get; }

        /// <summary>First 4 bytes of the resource file as little-endian 32-bit integer,
        /// or zero if it could vary.</summary>
        public abstract uint Signature { get; }

        /// <summary>Signatures peculiar to the resource (the one above is also included here).</summary>
        public IEnumerable<uint> Signatures { get; protected set; }

        /// <summary>Filename extensions peculiar to the resource.</summary>
        public IEnumerable<string> Extensions { get; protected set; }

        /// <summary>
        /// Create empty Entry that corresponds to implemented resource.
        /// </summary>
        public EntryType Create<EntryType> () where EntryType : Entry, new()
        {
            return new EntryType { Type = this.Type };
        }

        protected IResource ()
        {
            Extensions = new string[] { Tag.ToLowerInvariant() };
            Signatures = new uint[] { this.Signature };
        }

        public virtual ResourceOptions GetDefaultOptions ()
        {
            return null;
        }

        public virtual ResourceOptions GetOptions (object widget)
        {
            return GetDefaultOptions();
        }

        public virtual object GetCreationWidget ()
        {
            return null;
        }

        public virtual object GetAccessWidget ()
        {
            return null;
        }

        protected OptType GetOptions<OptType> (ResourceOptions res_options) where OptType : ResourceOptions
        {
            var options = res_options as OptType;
            if (null == options)
                options = this.GetDefaultOptions() as OptType;
            return options;
        }

        protected OptType Query<OptType> (string notice) where OptType : ResourceOptions
        {
            var args = new ParametersRequestEventArgs { Notice = notice };
            FormatCatalog.Instance.InvokeParametersRequest (this, args);
            if (!args.InputResult)
                throw new OperationCanceledException();

            return GetOptions<OptType> (args.Options);
        }
    }

    public class ResourceOptions
    {
    }

    public enum ArchiveOperation
    {
        Abort,
        Skip,
        Continue,
    }

    public delegate ArchiveOperation EntryCallback (int num, Entry entry, string description);

    public abstract class ArchiveFormat : IResource
    {
        public override string Type { get { return "archive"; } }

        public virtual bool CanCreate { get { return false; } }

        public abstract bool IsHierarchic { get; }

        public abstract ArcFile TryOpen (ArcView view);

        /// <summary>
        /// Extract file referenced by <paramref name="entry"/> into current directory.
        /// </summary>
        public void Extract (ArcFile file, Entry entry)
        {
            using (var reader = OpenEntry (file, entry))
                CopyEntry (file, reader, entry);
        }

        /// <summary>
        /// Open file referenced by <paramref name="entry"/> as Stream.
        /// </summary>
        public virtual Stream OpenEntry (ArcFile arc, Entry entry)
        {
            return arc.File.CreateStream (entry.Offset, entry.Size);
        }

        public virtual void CopyEntry (ArcFile arc, Stream input, Entry entry)
        {
            using (var output = CreateFile (entry.Name))
                input.CopyTo (output);
        }

        /// <summary>
        /// Create file corresponding to <paramref name="entry"/> in current directory and open it
        /// for writing. Overwrites existing file, if any.
        /// </summary>
        static public Stream CreateFile (string filename)
        {
            filename = CreatePath (filename);
            if (File.Exists (filename))
            {
                // query somehow whether to overwrite existing file or not.
            }
            return File.Create (filename);
        }

        static public string CreatePath (string filename)
        {
            string dir = Path.GetDirectoryName (filename);
            if (!string.IsNullOrEmpty (dir)) // check for malformed filenames
            {
                string root = Path.GetPathRoot (dir);
                if (!string.IsNullOrEmpty (root))
                {
                    dir = dir.Substring (root.Length); // strip root
                }
                string cwd = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
                dir = Path.GetFullPath (dir);
                filename = Path.GetFileName (filename);
                // check whether filename would reside within current directory
                if (dir.StartsWith (cwd, StringComparison.OrdinalIgnoreCase))
                {
                    // path looks legit, create it
                    Directory.CreateDirectory (dir);
                    filename = Path.Combine (dir, filename);
                }
            }
            return filename;
        }

        /// <summary>
        /// Create resource within stream <paramref name="file"/> containing entries from the
        /// supplied <paramref name="list"/> and applying necessary <paramref name="options"/>.
        /// </summary>
        public virtual void Create (Stream file, IEnumerable<Entry> list, ResourceOptions options = null,
                                    EntryCallback callback = null)
        {
            throw new NotImplementedException ("ArchiveFormat.Create is not implemented");
        }

        protected static bool IsSaneCount (int count)
        {
            return count > 0 && count < 0x20000;
        }
    }

    public delegate void ParametersRequestEventHandler (object sender, ParametersRequestEventArgs e);

    public class ParametersRequestEventArgs : EventArgs
    {
        /// <summary>
        /// String describing request nature (encryption key etc).
        /// </summary>
        public string Notice        { get; set; }

        /// <summary>
        /// Return value from ShowDialog()
        /// </summary>
        public bool   InputResult   { get; set; }

        /// <summary>
        /// Archive-specific options set by InputWidget.
        /// </summary>
        public ResourceOptions Options { get; set; }
    }

    public sealed class FormatCatalog
    {
        private static readonly FormatCatalog m_instance = new FormatCatalog();

        #pragma warning disable 649
        [ImportMany(typeof(ArchiveFormat))]
        private IEnumerable<ArchiveFormat>  m_arc_formats;
        [ImportMany(typeof(ImageFormat))]
        private IEnumerable<ImageFormat>    m_image_formats;
        [ImportMany(typeof(AudioFormat))]
        private IEnumerable<AudioFormat>    m_audio_formats;
        [ImportMany(typeof(ScriptFormat))]
        private IEnumerable<ScriptFormat>   m_script_formats;
        #pragma warning restore 649

        private MultiValueDictionary<string, IResource> m_extension_map = new MultiValueDictionary<string, IResource>();
        private MultiValueDictionary<uint, IResource> m_signature_map = new MultiValueDictionary<uint, IResource>();

        /// <summary> The only instance of this class.</summary>
        public static FormatCatalog       Instance      { get { return m_instance; } }

        public IEnumerable<ArchiveFormat> ArcFormats    { get { return m_arc_formats; } }
        public IEnumerable<ImageFormat>   ImageFormats  { get { return m_image_formats; } }
        public IEnumerable<AudioFormat>   AudioFormats  { get { return m_audio_formats; } }
        public IEnumerable<ScriptFormat>  ScriptFormats { get { return m_script_formats; } }

        public Exception LastError { get; set; }

        public event ParametersRequestEventHandler  ParametersRequest;

        private FormatCatalog ()
        {
            //An aggregate catalog that combines multiple catalogs
            var catalog = new AggregateCatalog();
            //Adds all the parts found in the same assembly as the Program class
            catalog.Catalogs.Add (new AssemblyCatalog (typeof(FormatCatalog).Assembly));
            //Adds parts matching pattern found in the directory of the assembly
            catalog.Catalogs.Add (new DirectoryCatalog (Path.GetDirectoryName (System.Reflection.Assembly.GetExecutingAssembly().Location), "Arc*.dll"));

            //Create the CompositionContainer with the parts in the catalog
            var container = new CompositionContainer (catalog);

            //Fill the imports of this object
            container.ComposeParts (this);
            AddResourceImpl (m_image_formats);
            AddResourceImpl (m_arc_formats);
            AddResourceImpl (m_audio_formats);
            AddResourceImpl (m_script_formats);
        }

        private void AddResourceImpl (IEnumerable<IResource> formats)
        {
            foreach (var impl in formats)
            {
                foreach (var ext in impl.Extensions)
                {
                    m_extension_map.Add (ext.ToUpperInvariant(), impl);
                }
                foreach (var signature in impl.Signatures)
                {
                    m_signature_map.Add (signature, impl);
                }
            }
        }

        /// <summary>
        /// Look up filename in format registry by filename extension and return corresponding interfaces.
        /// if no formats available, return empty range.
        /// </summary>
        public IEnumerable<IResource> LookupFileName (string filename)
        {
            string ext = Path.GetExtension (filename);
            if (string.IsNullOrEmpty (ext))
                return Enumerable.Empty<IResource>();
            return LookupExtension (ext.TrimStart ('.'));
        }

        public IEnumerable<IResource> LookupExtension (string ext)
        {
            return m_extension_map.GetValues (ext.ToUpperInvariant(), true);
        }

        public IEnumerable<Type> LookupExtension<Type> (string ext) where Type : IResource
        {
            return LookupExtension (ext).OfType<Type>();
        }

        public IEnumerable<IResource> LookupSignature (uint signature)
        {
            return m_signature_map.GetValues (signature, true);
        }

        public IEnumerable<Type> LookupSignature<Type> (uint signature) where Type : IResource
        {
            return LookupSignature (signature).OfType<Type>();
        }

        /// <summary>
        /// Create GameRes.Entry corresponding to <paramref name="filename"/> extension.
        /// <exception cref="System.ArgumentException">May be thrown if filename contains invalid
        /// characters.</exception>
        /// </summary>
        public EntryType Create<EntryType> (string filename) where EntryType : Entry, new()
        {
            EntryType entry = null;
            var formats = LookupFileName (filename);
            if (formats.Any())
                entry = formats.First().Create<EntryType>();
            if (null == entry)
                entry = new EntryType();
            entry.Name = filename;
            return entry;
        }

        public string GetTypeFromName (string filename)
        {
            var formats = LookupFileName (filename);
            if (formats.Any())
                return formats.First().Type;
            return "";
        }

        public void InvokeParametersRequest (object source, ParametersRequestEventArgs args)
        {
            if (null != ParametersRequest)
                ParametersRequest (source, args);
        }

        /// <summary>
        /// Read first 4 bytes from stream and return them as 32-bit signature.
        /// </summary>
        public static uint ReadSignature (Stream file)
        {
            file.Position = 0;
            uint signature = (byte)file.ReadByte();
            signature |= (uint)file.ReadByte() << 8;
            signature |= (uint)file.ReadByte() << 16;
            signature |= (uint)file.ReadByte() << 24;
            return signature;
        }
    }

    public class InvalidFormatException : FileFormatException
    {
        public InvalidFormatException() : base(garStrings.MsgInvalidFormat) { }
        public InvalidFormatException (string msg) : base (msg) { }
    }

    public class UnknownEncryptionScheme : Exception
    {
        public UnknownEncryptionScheme() : base(garStrings.MsgUnknownEncryption) { }
        public UnknownEncryptionScheme (string msg) : base (msg) { }
    }

    public class InvalidEncryptionScheme : Exception
    {
        public InvalidEncryptionScheme() : base(garStrings.MsgInvalidEncryption) { }
        public InvalidEncryptionScheme (string msg) : base (msg) { }
    }

    public class FileSizeException : Exception
    {
        public FileSizeException () : base (garStrings.MsgFileTooLarge) { }
        public FileSizeException (string msg) : base (msg) { }
    }

    public class InvalidFileName : Exception
    {
        public string FileName { get; set; }

        public InvalidFileName (string filename)
            : this (filename, garStrings.MsgInvalidFileName)
        {
        }

        public InvalidFileName (string filename, string message)
            : base (message)
        {
            FileName = filename;
        }

        public InvalidFileName (string filename, Exception X)
            : this (filename, garStrings.MsgInvalidFileName, X)
        {
        }

        public InvalidFileName (string filename, string message, Exception X)
            : base (message, X)
        {
            FileName = filename;
        }
    }
}
