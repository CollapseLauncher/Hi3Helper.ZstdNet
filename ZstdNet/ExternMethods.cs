using System;
using System.IO;
#if NET6_0_OR_GREATER
using System.Reflection;
using System.Runtime.Intrinsics.X86;
#else
using System.Diagnostics;
#endif
using System.Runtime.InteropServices;

namespace ZstdNet
{
    public static class DllUtils
    {
        private static readonly string CurrentProcPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        private static readonly string LibArchitecturePrefix = GetLibArchitecturePrefix();
        private static readonly string LibExtensionPrefix = GetLibExtensionPrefix();
        private static readonly string LibPlatformNamePrefix = GetLibPlatformNamePrefix();
        private static readonly string LibFolderPath = Path.Combine("Lib", $"{LibPlatformNamePrefix}-{LibArchitecturePrefix}");
        private static readonly string LibFullPath = Path.Combine(CurrentProcPath, LibFolderPath, "{0}" + LibExtensionPrefix);

        private static string GetLibArchitecturePrefix() => RuntimeInformation.ProcessArchitecture.ToString().ToLower();

        private static string GetLibExtensionPrefix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ".dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ".so";

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : string.Empty;
        }

        private static string GetLibPlatformNamePrefix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "win";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "linux";

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "unknown";
        }

#if !NET6_0_OR_GREATER
        public static void SetWinDllDirectory()
        {
            string path = Path.Combine(CurrentProcPath, LibFolderPath);
            if(!SetDllDirectory(path))
                Trace.TraceWarning($"{nameof(ZstdNet)}: Failed to set DLL directory to '{path}'");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);
#else
        public static bool IsIgnoreMissingLibrary = false;

        public static bool IsLibraryExist(string libraryName) => IsIgnoreMissingLibrary || File.Exists(string.Format(LibFullPath, libraryName));

#if NET6_0_OR_GREATER
        public static void ThrowIfDllNotExist()
        {
            if (!IsLibraryExist(DllName))
                throw new DllNotFoundException("libzstd.dll is not found!");
        }
#endif

        internal static nint DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            string libraryNameLoad = Bmi2.IsSupported ?
                string.Format(LibFullPath, libraryName + "-bmi2") :
                string.Format(LibFullPath, libraryName);

            // Try load the library and if fails, then throw.
            bool isLoadSuccessful = NativeLibrary.TryLoad(libraryNameLoad, assembly, searchPath, out nint pResult);
            if (isLoadSuccessful && pResult != 0)
            {
                return pResult;
            }

            // Throw if fails on loading standard libzstd while Bmi2 is not supported and no fallback available.
            if (!Bmi2.IsSupported)
            {
                goto Fail;
            }

            // If loading Bmi2 lib is failing, try to load the standard one.
            libraryNameLoad = string.Format(LibFullPath, libraryName);
            isLoadSuccessful = NativeLibrary.TryLoad(libraryNameLoad, assembly, searchPath, out pResult);

            // If it still fails, throw.
            if (!isLoadSuccessful || pResult == 0)
            {
                goto Fail;
            }

            return pResult;

        Fail:
            throw new FileLoadException($"Failed while loading library from this path: {libraryName}\r\nMake sure that the library/{GetLibExtensionPrefix()} is exist or valid and not corrupted!");
        }
#endif

        public const string DllName = "libzstd";
    }

    internal static class ExternMethods
    {
        static ExternMethods()
        {
#if !NET6_0_OR_GREATER
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                DllUtils.SetWinDllDirectory();
#else
            // Use custom Dll import resolver
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllUtils.DllImportResolver);
#endif
        }

        public static nuint ZDICT_trainFromBuffer(Span<byte> dictBuffer, nuint dictBufferCapacity, Span<byte> samplesBuffer, Span<nuint> samplesSizes, uint nbSamples)
			=> ZDICT_trainFromBuffer(ref MemoryMarshal.GetReference(dictBuffer),
	                                 dictBufferCapacity,
	                                 ref MemoryMarshal.GetReference(samplesBuffer),
	                                 ref MemoryMarshal.GetReference(samplesSizes),
	                                 nbSamples);

		[DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZDICT_trainFromBuffer(ref byte dictBuffer, nuint dictBufferCapacity, ref byte samplesBuffer, ref nuint samplesSizes, uint nbSamples);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZDICT_isError(nuint code);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZDICT_getErrorName(nuint code);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createCCtx();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeCCtx(nint cctx);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createDCtx();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeDCtx(nint cctx);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compressCCtx(nint ctx, nint dst, nuint dstCapacity, nint src, nuint srcSize, int compressionLevel);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compressCCtx(nint ctx, ref byte dst, nuint dstCapacity, ref byte src, nuint srcSize, int compressionLevel);
        public static nuint ZSTD_compressCCtx(nint ctx, Span<byte> dst, nuint dstCapacity, ReadOnlySpan<byte> src, nuint srcSize, int compressionLevel)
            => ZSTD_compressCCtx(ctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize, compressionLevel);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_decompressDCtx(nint ctx, nint dst, nuint dstCapacity, nint src, nuint srcSize);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_decompressDCtx(nint ctx, ref byte dst, nuint dstCapacity, ref byte src, nuint srcSize);
        public static nuint ZSTD_decompressDCtx(nint ctx, Span<byte> dst, nuint dstCapacity, ReadOnlySpan<byte> src, nuint srcSize)
            => ZSTD_decompressDCtx(ctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compress2(nint ctx, ref byte dst, nuint dstCapacity, ref byte src, nuint srcSize);
        public static nuint ZSTD_compress2(nint ctx, Span<byte> dst, nuint dstCapacity, ReadOnlySpan<byte> src, nuint srcSize)
            => ZSTD_compress2(ctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createCDict(byte[] dict, nuint dictSize, int compressionLevel);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeCDict(nint cdict);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compress_usingCDict(nint cctx, nint dst, nuint dstCapacity, nint src, nuint srcSize, nint cdict);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compress_usingCDict(nint cctx, ref byte dst, nuint dstCapacity, ref byte src, nuint srcSize, nint cdict);
        public static nuint ZSTD_compress_usingCDict(nint cctx, Span<byte> dst, nuint dstCapacity, ReadOnlySpan<byte> src, nuint srcSize, nint cdict)
            => ZSTD_compress_usingCDict(cctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize, cdict);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createDDict(byte[] dict, nuint dictSize);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeDDict(nint ddict);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_decompress_usingDDict(nint dctx, nint dst, nuint dstCapacity, nint src, nuint srcSize, nint ddict);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_decompress_usingDDict(nint dctx, ref byte dst, nuint dstCapacity, ref byte src, nuint srcSize, nint ddict);
        public static nuint ZSTD_decompress_usingDDict(nint dctx, Span<byte> dst, nuint dstCapacity, ReadOnlySpan<byte> src, nuint srcSize, nint ddict)
            => ZSTD_decompress_usingDDict(dctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize, ddict);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_getDecompressedSize(nint src, nuint srcSize);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_getFrameContentSize(nint src, nuint srcSize);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_getFrameContentSize(ref byte src, nuint srcSize);
        public static ulong ZSTD_getFrameContentSize(ReadOnlySpan<byte> src, nuint srcSize)
            => ZSTD_getFrameContentSize(ref MemoryMarshal.GetReference(src), srcSize);

        public const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
        public const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ZSTD_maxCLevel();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ZSTD_minCLevel();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compressBound(nuint srcSize);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZSTD_isError(nuint code);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_getErrorName(nuint code);

        #region Advanced APIs

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_CCtx_reset(nint cctx, ZSTD_ResetDirective reset);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZSTD_bounds ZSTD_cParam_getBounds(ZSTD_cParameter cParam);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_CCtx_setParameter(nint cctx, ZSTD_cParameter param, int value);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_DCtx_reset(nint dctx, ZSTD_ResetDirective reset);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZSTD_bounds ZSTD_dParam_getBounds(ZSTD_dParameter dParam);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_DCtx_setParameter(nint dctx, ZSTD_dParameter param, int value);


        [StructLayout(LayoutKind.Sequential)]
        internal struct ZSTD_bounds
        {
            public nuint error;
            public int lowerBound;
            public int upperBound;
        }

        public enum ZSTD_ResetDirective
        {
            ZSTD_reset_session_only = 1,
            ZSTD_reset_parameters = 2,
            ZSTD_reset_session_and_parameters = 3
        }

        #endregion

        #region Streaming APIs

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createCStream();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeCStream(nint zcs);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_initCStream(nint zcs, int compressionLevel);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compressStream(nint zcs, ref ZSTD_Buffer output, ref ZSTD_Buffer input);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_flushStream(nint zcs, ref ZSTD_Buffer output);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_endStream(nint zcs, ref ZSTD_Buffer output);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_CStreamInSize();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_CStreamOutSize();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ZSTD_createDStream();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_freeDStream(nint zds);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_initDStream(nint zds);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_decompressStream(nint zds, ref ZSTD_Buffer output, ref ZSTD_Buffer input);
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_DStreamInSize();
        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_DStreamOutSize();

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_compressStream2(nint zcs, ref ZSTD_Buffer output, ref ZSTD_Buffer input, ZSTD_EndDirective endOp);

        public enum ZSTD_EndDirective
        {
            ZSTD_e_continue = 0,
            ZSTD_e_flush = 1,
            ZSTD_e_end = 2
        }

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_initDStream_usingDDict(nint zds, nint dict);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_initCStream_usingCDict(nint zds, nint dict);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_CCtx_refCDict(nint cctx, nint cdict);

        [DllImport(DllUtils.DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint ZSTD_DCtx_refDDict(nint cctx, nint cdict);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ZSTD_Buffer
        {
            public ZSTD_Buffer(nuint pos, nuint size)
            {
                buffer = 0;
                this.size = size;
                this.pos = pos;
            }

            public nint buffer;
            public nuint size;
            public nuint pos;

			public bool IsFullyConsumed => size <= pos;
		}

#endregion
    }

    public enum ZSTD_cParameter
    {
        // compression parameters
        ZSTD_c_compressionLevel = 100,
        ZSTD_c_windowLog = 101,
        ZSTD_c_hashLog = 102,
        ZSTD_c_chainLog = 103,
        ZSTD_c_searchLog = 104,
        ZSTD_c_minMatch = 105,
        ZSTD_c_targetLength = 106,
        ZSTD_c_strategy = 107,

		ZSTD_c_targetCBlockSize = 130,

		// long distance matching mode parameters
		ZSTD_c_enableLongDistanceMatching = 160,
		ZSTD_c_ldmHashLog = 161,
		ZSTD_c_ldmMinMatch = 162,
		ZSTD_c_ldmBucketSizeLog = 163,
		ZSTD_c_ldmHashRateLog = 164,

        // frame parameters
        ZSTD_c_contentSizeFlag = 200,
        ZSTD_c_checksumFlag = 201,
        ZSTD_c_dictIDFlag = 202,

        // multi-threading parameters
        ZSTD_c_nbWorkers = 400,
        ZSTD_c_jobSize = 401,
        ZSTD_c_overlapLog = 402
    }

    public enum ZSTD_dParameter
    {
        ZSTD_d_windowLogMax = 100
    }

	public enum ZSTD_ErrorCode
	{
		ZSTD_error_no_error = 0,
		ZSTD_error_GENERIC = 1,
		ZSTD_error_prefix_unknown = 10,
		ZSTD_error_version_unsupported = 12,
		ZSTD_error_frameParameter_unsupported = 14,
		ZSTD_error_frameParameter_windowTooLarge = 16,
		ZSTD_error_corruption_detected = 20,
		ZSTD_error_checksum_wrong = 22,
		ZSTD_error_literals_headerWrong = 24,
		ZSTD_error_dictionary_corrupted = 30,
		ZSTD_error_dictionary_wrong = 32,
		ZSTD_error_dictionaryCreation_failed = 34,
		ZSTD_error_parameter_unsupported = 40,
		ZSTD_error_parameter_combination_unsupported = 41,
		ZSTD_error_parameter_outOfBound = 42,
		ZSTD_error_tableLog_tooLarge = 44,
		ZSTD_error_maxSymbolValue_tooLarge = 46,
		ZSTD_error_maxSymbolValue_tooSmall = 48,
		ZSTD_error_cannotProduce_uncompressedBlock = 49,
		ZSTD_error_stabilityCondition_notRespected = 50,
		ZSTD_error_stage_wrong = 60,
		ZSTD_error_init_missing = 62,
		ZSTD_error_memory_allocation = 64,
		ZSTD_error_workSpace_tooSmall = 66,
		ZSTD_error_dstSize_tooSmall = 70,
		ZSTD_error_srcSize_wrong = 72,
		ZSTD_error_dstBuffer_null = 74,
		ZSTD_error_noForwardProgress_destFull = 80,
		ZSTD_error_noForwardProgress_inputEmpty = 82
	}
}
