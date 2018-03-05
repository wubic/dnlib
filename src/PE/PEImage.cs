// dnlib: See LICENSE.txt for more info

using System;
using System.Collections.Generic;
using System.IO;
using dnlib.IO;
using dnlib.Utils;
using dnlib.W32Resources;
using dnlib.Threading;

namespace dnlib.PE {
	/// <summary>
	/// Image layout
	/// </summary>
	public enum ImageLayout {
		/// <summary>
		/// Use this if the PE file has a normal structure (eg. it's been read from a file on disk)
		/// </summary>
		File,

		/// <summary>
		/// Use this if the PE file has been loaded into memory by the OS PE file loader
		/// </summary>
		Memory,
	}

	/// <summary>
	/// Accesses a PE file
	/// </summary>
	public sealed class PEImage : IPEImage {
		// Default to false because an OS loaded PE image may contain memory holes. If there
		// are memory holes, other code (eg. .NET resource creator) must verify that all memory
		// is available, which will be slower.
		const bool USE_MEMORY_LAYOUT_WITH_MAPPED_FILES = false;

		static readonly IPEType MemoryLayout = new MemoryPEType();
		static readonly IPEType FileLayout = new FilePEType();

		IImageStream imageStream;
		IImageStreamCreator imageStreamCreator;
		IPEType peType;
		PEInfo peInfo;
		UserValue<Win32Resources> win32Resources;
#if THREAD_SAFE
		readonly Lock theLock = Lock.Create();
#endif

		sealed class FilePEType : IPEType {
			/// <inheritdoc/>
			public RVA ToRVA(PEInfo peInfo, FileOffset offset) => peInfo.ToRVA(offset);

			/// <inheritdoc/>
			public FileOffset ToFileOffset(PEInfo peInfo, RVA rva) => peInfo.ToFileOffset(rva);
		}

		sealed class MemoryPEType : IPEType {
			/// <inheritdoc/>
			public RVA ToRVA(PEInfo peInfo, FileOffset offset) => (RVA)offset;

			/// <inheritdoc/>
			public FileOffset ToFileOffset(PEInfo peInfo, RVA rva) => (FileOffset)rva;
		}

		/// <inheritdoc/>
		public bool IsFileImageLayout => peType is FilePEType;

		/// <inheritdoc/>
		public bool MayHaveInvalidAddresses => !IsFileImageLayout;

		/// <inheritdoc/>
		public string FileName => imageStreamCreator.FileName;

		/// <inheritdoc/>
		public ImageDosHeader ImageDosHeader => peInfo.ImageDosHeader;

		/// <inheritdoc/>
		public ImageNTHeaders ImageNTHeaders => peInfo.ImageNTHeaders;

		/// <inheritdoc/>
		public IList<ImageSectionHeader> ImageSectionHeaders => peInfo.ImageSectionHeaders;

		/// <inheritdoc/>
		public IList<ImageDebugDirectory> ImageDebugDirectories {
			get {
				if (imageDebugDirectories == null)
					imageDebugDirectories = ReadImageDebugDirectories();
				return imageDebugDirectories;
			}
		}
		ImageDebugDirectory[] imageDebugDirectories;

		/// <inheritdoc/>
		public Win32Resources Win32Resources {
			get => win32Resources.Value;
			set {
				IDisposable origValue = null;
				if (win32Resources.IsValueInitialized) {
					origValue = win32Resources.Value;
					if (origValue == value)
						return;
				}
				win32Resources.Value = value;

				if (origValue != null)
					origValue.Dispose();
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="imageStreamCreator">The PE stream creator</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IImageStreamCreator imageStreamCreator, ImageLayout imageLayout, bool verify) {
			try {
				this.imageStreamCreator = imageStreamCreator;
				peType = ConvertImageLayout(imageLayout);
				ResetReader();
				peInfo = new PEInfo(imageStream, verify);
				Initialize();
			}
			catch {
				Dispose();
				throw;
			}
		}

		void Initialize() {
			win32Resources.ReadOriginalValue = () => {
				var dataDir = peInfo.ImageNTHeaders.OptionalHeader.DataDirectories[2];
				if (dataDir.VirtualAddress == 0 || dataDir.Size == 0)
					return null;
				return new Win32ResourcesPE(this);
			};
#if THREAD_SAFE
			win32Resources.Lock = theLock;
#endif
		}

		static IPEType ConvertImageLayout(ImageLayout imageLayout) {
			switch (imageLayout) {
			case ImageLayout.File: return FileLayout;
			case ImageLayout.Memory: return MemoryLayout;
			default: throw new ArgumentException("imageLayout");
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		/// <param name="mapAsImage"><c>true</c> if we should map it as an executable</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(string fileName, bool mapAsImage, bool verify)
			: this(ImageStreamCreator.Create(fileName, mapAsImage), mapAsImage ? ImageLayout.Memory : ImageLayout.File, verify) {
			try {
				if (mapAsImage && imageStreamCreator is MemoryMappedFileStreamCreator) {
					((MemoryMappedFileStreamCreator)imageStreamCreator).Length = peInfo.GetImageSize();
					ResetReader();
				}
			}
			catch {
				Dispose();
				throw;
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(string fileName, bool verify)
			: this(fileName, USE_MEMORY_LAYOUT_WITH_MAPPED_FILES, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		public PEImage(string fileName)
			: this(fileName, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="filename">Filename or null</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, string filename, ImageLayout imageLayout, bool verify)
			: this(new MemoryStreamCreator(data) { FileName = filename }, imageLayout, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, ImageLayout imageLayout, bool verify)
			: this(data, null, imageLayout, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, bool verify)
			: this(data, null, ImageLayout.File, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="filename">Filename or null</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, string filename, bool verify)
			: this(data, filename, ImageLayout.File, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		public PEImage(byte[] data)
			: this(data, null, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="filename">Filename or null</param>
		public PEImage(byte[] data, string filename)
			: this(data, filename, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, long length, ImageLayout imageLayout, bool verify)
			: this(new UnmanagedMemoryStreamCreator(baseAddr, length), imageLayout, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, long length, bool verify)
			: this(baseAddr, length, ImageLayout.Memory, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		public PEImage(IntPtr baseAddr, long length)
			: this(baseAddr, length, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, ImageLayout imageLayout, bool verify)
			: this(new UnmanagedMemoryStreamCreator(baseAddr, 0x10000), imageLayout, verify) {
			try {
				((UnmanagedMemoryStreamCreator)imageStreamCreator).Length = peInfo.GetImageSize();
				ResetReader();
			}
			catch {
				Dispose();
				throw;
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, bool verify)
			: this(baseAddr, ImageLayout.Memory, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		public PEImage(IntPtr baseAddr)
			: this(baseAddr, true) {
		}

		void ResetReader() {
			if (imageStream != null) {
				imageStream.Dispose();
				imageStream = null;
			}
			imageStream = imageStreamCreator.CreateFull();
		}

		/// <inheritdoc/>
		public RVA ToRVA(FileOffset offset) => peType.ToRVA(peInfo, offset);

		/// <inheritdoc/>
		public FileOffset ToFileOffset(RVA rva) => peType.ToFileOffset(peInfo, rva);

		/// <inheritdoc/>
		public void Dispose() {
			IDisposable id;
			if (win32Resources.IsValueInitialized && (id = win32Resources.Value) != null)
				id.Dispose();
			if ((id = imageStream) != null)
				id.Dispose();
			if ((id = imageStreamCreator) != null)
				id.Dispose();
			win32Resources.Value = null;
			imageStream = null;
			imageStreamCreator = null;
			peType = null;
			peInfo = null;
		}

		/// <inheritdoc/>
		public IImageStream CreateStream(FileOffset offset) {
			if ((long)offset > imageStreamCreator.Length)
				throw new ArgumentOutOfRangeException(nameof(offset));
			long length = imageStreamCreator.Length - (long)offset;
			return CreateStream(offset, length);
		}

		/// <inheritdoc/>
		public IImageStream CreateStream(FileOffset offset, long length) => imageStreamCreator.Create(offset, length);

		/// <inheritdoc/>
		public IImageStream CreateFullStream() => imageStreamCreator.CreateFull();

		/// <inheritdoc/>
		public void UnsafeDisableMemoryMappedIO() {
			if (imageStreamCreator is MemoryMappedFileStreamCreator creator)
				creator.UnsafeDisableMemoryMappedIO();
		}

		/// <inheritdoc/>
		public bool IsMemoryMappedIO {
			get {
				var creator = imageStreamCreator as MemoryMappedFileStreamCreator;
				return creator == null ? false : creator.IsMemoryMappedIO;
			}
		}

		ImageDebugDirectory[] ReadImageDebugDirectories() {
			try {
				if (6 >= ImageNTHeaders.OptionalHeader.DataDirectories.Length)
					return Array2.Empty<ImageDebugDirectory>();
				var dataDir = ImageNTHeaders.OptionalHeader.DataDirectories[6];
				if (dataDir.VirtualAddress == 0)
					return Array2.Empty<ImageDebugDirectory>();
				using (var reader = imageStream.Clone()) {
					if (dataDir.Size > reader.Length)
						return Array2.Empty<ImageDebugDirectory>();
					int count = (int)(dataDir.Size / 0x1C);
					if (count == 0)
						return Array2.Empty<ImageDebugDirectory>();
					reader.Position = (long)ToFileOffset(dataDir.VirtualAddress);
					if (reader.Position + dataDir.Size > reader.Length)
						return Array2.Empty<ImageDebugDirectory>();
					var res = new ImageDebugDirectory[count];
					for (int i = 0; i < res.Length; i++)
						res[i] = new ImageDebugDirectory(reader, true);
					return res;
				}
			}
			catch (IOException) {
			}
			return Array2.Empty<ImageDebugDirectory>();
		}
	}
}
