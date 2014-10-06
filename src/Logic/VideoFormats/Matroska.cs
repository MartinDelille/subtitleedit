using System;
using System.Collections.Generic;
using System.IO;

namespace Nikse.SubtitleEdit.Logic.VideoFormats
{
    public class SubtitleSequence
    {
        public long StartMilliseconds { get; set; }
        public long EndMilliseconds { get; set; }
        public byte[] BinaryData { get; set; }

        public SubtitleSequence(byte[] data, long startMilliseconds, long endMilliseconds)
        {
            BinaryData = data;
            StartMilliseconds = startMilliseconds;
            EndMilliseconds = endMilliseconds;
        }

        public string Text
        {
            get
            {
                if (BinaryData != null)
                    return System.Text.Encoding.UTF8.GetString(BinaryData).Replace("\\N", Environment.NewLine);
                return string.Empty;
            }
        }
    }

    public class MatroskaSubtitleInfo
    {
        public long TrackNumber { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string CodecId { get; set; }
        public string CodecPrivate { get; set; }
        public int ContentCompressionAlgorithm { get; set; }
        public int ContentEncodingType { get; set; }
    }

    public class MatroskaTrackInfo
    {
        public int TrackNumber { get; set; }
        public string Uid { get; set; }
        public bool IsVideo { get; set; }
        public bool IsAudio { get; set; }
        public bool IsSubtitle { get; set; }
        public string CodecId { get; set; }
        public string CodecPrivate { get; set; }
        public int DefaultDuration { get; set; }
        public string Language { get; set; }
    }

    public class Matroska : IDisposable
    {
        private enum ElementId : uint
        {
            Ebml = 0x1A45DFA3,
            Void = 0xEC,
            Crc32 = 0xBF,
            Segment = 0x18538067,
            SeekHead = 0x114D9B74,
            
            Info = 0x1549A966,
            TimecodeScale = 0x2AD7B1,
            Duration = 0x4489,

            Tracks = 0x1654AE6B,
            TrackEntry = 0xAE,
            TrackNumber = 0xD7,
            TrackType = 0x83,
            DefaultDuration = 0x23E383,
            Name = 0x536E,
            Language = 0x22B59C,
            CodecId = 0x86,
            CodecPrivate = 0x63A2,
            Video = 0xE0,
            PixelWidth = 0xB0,
            PixelHeight = 0xBA,
            Audio = 0xE1,
            ContentEncodings = 0x6D80,
            ContentEncodingOrder = 0x5031,
            ContentEncodingScope = 0x5032,
            ContentEncodingType = 0x5033,
            ContentCompression = 0x5034,
            ContentCompAlgo = 0x4254,
            ContentCompSettings = 0x4255,

            Cluster = 0x1F43B675,
            Timecode = 0xE7,
            SimpleBlock = 0xA3,
            BlockGroup = 0xA0,
            Block = 0xA1,
            BlockDuration = 0x9B,

            Cues = 0x1C53BB6B,
            Attachments = 0x1941A469,
            Chapters = 0x1043A770,
            Tags = 0x1254C367
        }

        public delegate void LoadMatroskaCallback(long position, long total);

        private readonly string _fileName;
        private readonly FileStream _stream;
        private readonly long _streamLength;
        private readonly bool _valid;
        private int _pixelWidth, _pixelHeight;
        private double _frameRate;
        private string _videoCodecId;
        private double _durationInMilliseconds;

        private List<MatroskaSubtitleInfo> _subtitleList;
        private int _subtitleRipTrackNumber;
        private List<SubtitleSequence> _subtitleRip = new List<SubtitleSequence>();
        private long _timecodeScale = 1000000; // Timestamp scale in nanoseconds (1.000.000 means all timestamps in the segment are expressed in milliseconds).
        private List<MatroskaTrackInfo> _tracks;

        public Matroska(string fileName)
        {
            _fileName = fileName;
            _stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _streamLength = _stream.Length;
            _valid = ReadEbmlId() == ElementId.Ebml; // matroska file must start with ebml header
        }

        public bool IsValid
        {
            get
            {
                return _valid;
            }
        }

        public string FileName
        {
            get
            {
                return _fileName;
            }
        }

        public List<MatroskaTrackInfo> GetTrackInfo()
        {
            _tracks = new List<MatroskaTrackInfo>();
            
            // skip header
            _stream.Position = 4;
            var elementSize = (long)ReadVariableLengthUInt();
            _stream.Seek(elementSize, SeekOrigin.Current);

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(afterPosition);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks();
                        break;
                    case ElementId.Segment:
                        continue;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            return _tracks;
        }

        /// <summary>
        /// Get first time of track
        /// </summary>
        /// <param name="trackNumber">Track number</param>
        /// <returns>Start time in milliseconds</returns>
        public long GetTrackStartTime(int trackNumber)
        {
            _tracks = new List<MatroskaTrackInfo>();
            
            // skip header
            _stream.Position = 4;
            var elementSize = (long)ReadVariableLengthUInt();
            _stream.Seek(elementSize, SeekOrigin.Current);

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(afterPosition);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks();
                        break;
                    case ElementId.Cluster:
                        return FindTrackStartInCluster(trackNumber);
                    case ElementId.Segment:
                        continue;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            return 0;
        }

        private long FindTrackStartInCluster(int targetTrackNumber)
        {
            long clusterTimeCode = 0;
            int trackStartTime = -1;

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Timecode:
                        // Absolute timestamp of the cluster (based on TimecodeScale)
                        clusterTimeCode = (long)ReadUInt((int)elementSize);
                        break;
                    case ElementId.BlockGroup:
                        AnalyzeMatroskaBlock(clusterTimeCode);
                        break;
                    case ElementId.SimpleBlock:
                        var trackNumber = (int)ReadVariableLengthUInt();
                        if (trackNumber == targetTrackNumber)
                        {
                            // Timecode (relative to Cluster timecode, signed int16)
                            trackStartTime = ReadInt16();
                            _stream.Position = _streamLength; // break while
                        }
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            return (clusterTimeCode + trackStartTime) * _timecodeScale / 1000000;
        }

        private void AnalyzeMatroskaTrackVideo(long endPosition)
        {
            ElementId elementId;
            while (_stream.Position < endPosition && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.PixelWidth:
                        _pixelWidth = (int)ReadUInt((int)elementSize);
                        break;
                    case ElementId.PixelHeight:
                        _pixelHeight = (int)ReadUInt((int)elementSize);
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }
        }

        private string GetMatroskaString(long size)
        {
            try
            {
                byte[] buffer = new byte[size];
                _stream.Read(buffer, 0, (int)size);
                return System.Text.Encoding.UTF8.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        private void AnalyzeMatroskaTrackEntry()
        {
            long defaultDuration = 0;
            bool isVideo = false;
            bool isAudio = false;
            bool isSubtitle = false;
            long trackNumber = 0;
            string name = string.Empty;
            string language = string.Empty;
            string codecId = string.Empty;
            string codecPrivate = string.Empty;
            //var biCompression = string.Empty;
            int contentCompressionAlgorithm = -1;
            int contentEncodingType = -1;

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.DefaultDuration:
                        defaultDuration = (int)ReadUInt((int)elementSize);
                        break;
                    case ElementId.Video:
                        AnalyzeMatroskaTrackVideo(afterPosition);
                        isVideo = true;
                        break;
                    case ElementId.Audio:
                        AnalyzeMatroskaTrackVideo(afterPosition);
                        isAudio = true;
                        break;
                    case ElementId.TrackNumber:
                        trackNumber = (long)ReadUInt((int)elementSize);
                        break;
                    case ElementId.Name:
                        name = GetMatroskaString(elementSize);
                        break;
                    case ElementId.Language:
                        language = GetMatroskaString(elementSize);
                        break;
                    case ElementId.CodecId:
                        codecId = GetMatroskaString(elementSize);
                        break;
                    case ElementId.TrackType:
                        if (elementSize == 1)
                        {
                            var trackType = (byte)_stream.ReadByte();
                            if (trackType == 0x11) // subtitle
                                isSubtitle = true;
                            if (trackType == 1)
                                isVideo = true;
                            if (trackType == 2)
                                isAudio = true;
                        }
                        break;
                    case ElementId.CodecPrivate:
                        codecPrivate = GetMatroskaString(elementSize);
                        //if (codecPrivate.Length > 20)
                        //    biCompression = codecPrivate.Substring(16, 4);
                        break;
                    case ElementId.ContentEncodings:
                        contentCompressionAlgorithm = 0; // default value
                        contentEncodingType = 0; // default value

                        int contentEncoding1 = _stream.ReadByte();
                        int contentEncoding2 = _stream.ReadByte();

                        if (contentEncoding1 == 0x62 && contentEncoding2 == 0x40)
                        {
                            AnalyzeMatroskaContentEncoding(afterPosition, ref contentCompressionAlgorithm, ref contentEncodingType);
                        }
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            if (_tracks != null)
            {
                _tracks.Add(new MatroskaTrackInfo
                {
                    TrackNumber = (int)trackNumber,
                    IsVideo = isVideo,
                    IsAudio = isAudio,
                    IsSubtitle = isSubtitle,
                    Language = language,
                    CodecId = codecId,
                    CodecPrivate = codecPrivate,
                });
            }
            if (isVideo)
            {
                if (defaultDuration > 0)
                    _frameRate = 1.0 / (defaultDuration / 1000000000.0);
                _videoCodecId = codecId;
            }
            else if (isSubtitle)
            {
                _subtitleList.Add(new MatroskaSubtitleInfo
                {
                    Name = name,
                    TrackNumber = trackNumber,
                    CodecId = codecId,
                    Language = language,
                    CodecPrivate = codecPrivate,
                    ContentEncodingType = contentEncodingType,
                    ContentCompressionAlgorithm = contentCompressionAlgorithm
                });
            }
        }

        private void AnalyzeMatroskaContentEncoding(long endPosition, ref int contentCompressionAlgorithm, ref int contentEncodingType)
        {
            ElementId elementId;
            while (_stream.Position < endPosition && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.ContentEncodingOrder:
                        var contentEncodingOrder = ReadUInt((int)elementSize);
                        System.Diagnostics.Debug.WriteLine("ContentEncodingOrder: " + contentEncodingOrder);
                        break;
                    case ElementId.ContentEncodingScope:
                        var contentEncodingScope = ReadUInt((int)elementSize);
                        System.Diagnostics.Debug.WriteLine("ContentEncodingScope: " + contentEncodingScope);
                        break;
                    case ElementId.ContentEncodingType:
                        contentEncodingType = (int)ReadUInt((int)elementSize);
                        break;
                    case ElementId.ContentCompression:
                        while (_stream.Position < afterPosition)
                        {
                            elementId = ReadEbmlId();
                            elementSize = (long)ReadVariableLengthUInt();
                            switch (elementId)
                            {
                                case ElementId.ContentCompAlgo:
                                    contentCompressionAlgorithm = (int)ReadUInt((int)elementSize);
                                    break;
                                case ElementId.ContentCompSettings:
                                    var contentCompSettings = ReadUInt((int)elementSize);
                                    System.Diagnostics.Debug.WriteLine("ContentCompSettings: " + contentCompSettings);
                                    break;
                            }
                        }
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }
        }

        private void AnalyzeMatroskaSegmentInformation(long endPosition)
        {
            var duration = 0.0;

            ElementId elementId;
            while (_stream.Position < endPosition && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.TimecodeScale: // Timestamp scale in nanoseconds (1.000.000 means all timestamps in the segment are expressed in milliseconds)
                        _timecodeScale = (int)ReadUInt((int)elementSize);
                        break;
                    case ElementId.Duration: // Duration of the segment (based on TimecodeScale)
                        duration = elementSize == 4 ? ReadFloat32() : ReadFloat64();
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            if (_timecodeScale > 0 && duration > 0)
                _durationInMilliseconds = duration / _timecodeScale * 1000000.0;
            else if (duration > 0)
                _durationInMilliseconds = duration;
        }

        private void AnalyzeMatroskaTracks()
        {
            _subtitleList = new List<MatroskaSubtitleInfo>();

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                if (elementId == ElementId.TrackEntry)
                {
                    AnalyzeMatroskaTrackEntry();
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }
        }

        public void GetMatroskaInfo(out bool hasConstantFrameRate, out double frameRate, out int pixelWidth, out int pixelHeight, out double millisecondDuration, out string videoCodec)
        {
            _durationInMilliseconds = 0;

            // skip header
            _stream.Position = 4;
            var elementSize = (long)ReadVariableLengthUInt();
            _stream.Seek(elementSize, SeekOrigin.Current);

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(afterPosition);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks();
                        break;
                    case ElementId.Cluster:
                        AnalyzeMatroskaCluster();
                        break;
                    case ElementId.Segment:
                        continue;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            pixelWidth = _pixelWidth;
            pixelHeight = _pixelHeight;
            frameRate = _frameRate;
            hasConstantFrameRate = _frameRate > 0;
            millisecondDuration = _durationInMilliseconds;
            videoCodec = _videoCodecId;
        }

        private void AnalyzeMatroskaCluster()
        {
            long clusterTimeCode = 0;
            const long duration = 0;

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                var elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Timecode:
                        clusterTimeCode = (long)ReadUInt((int)elementSize);
                        break;
                    case ElementId.BlockGroup:
                        AnalyzeMatroskaBlock(clusterTimeCode);
                        break;
                    case ElementId.SimpleBlock:
                        long before = _stream.Position;
                        var trackNumber = (int)ReadVariableLengthUInt();
                        if (trackNumber == _subtitleRipTrackNumber)
                        {
                            int timeCode = ReadInt16();

                            // lacing
                            var flags = (byte)_stream.ReadByte();
                            byte numberOfFrames;
                            switch ((flags & 6)) // 6 = 00000110
                            {
                                case 0:
                                    System.Diagnostics.Debug.Print("No lacing"); // No lacing
                                    break;
                                case 2:
                                    System.Diagnostics.Debug.Print("Xiph lacing"); // 2 = 00000010 = Xiph lacing
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    break;
                                case 4:
                                    System.Diagnostics.Debug.Print("fixed-size"); // 4 = 00000100 = Fixed-size lacing
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    for (int i = 1; i <= numberOfFrames; i++)
                                        _stream.ReadByte(); // frames
                                    break;
                                case 6:
                                    System.Diagnostics.Debug.Print("EBML"); // 6 = 00000110 = EMBL
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    break;
                            }

                            var buffer = new byte[elementSize - (_stream.Position - before)];
                            _stream.Read(buffer, 0, buffer.Length);
                            _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));
                        }
                        break;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }
        }

        private void AnalyzeMatroskaBlock(long clusterTimeCode)
        {
            var elementId = ReadEbmlId();
            if (elementId != ElementId.Block)
            {
                return;
            }
            var elementSize = (long)ReadVariableLengthUInt();
            var afterPosition = _stream.Position + elementSize;

            var trackNumber = (int)ReadVariableLengthUInt();
            var timeCode = ReadInt16();

            // lacing
            var flags = (byte)_stream.ReadByte();
            byte numberOfFrames;
            switch (flags & 6)
            {
                case 0: // 00000000 = No lacing
                    System.Diagnostics.Debug.Print("No lacing");
                    break;
                case 2: // 00000010 = Xiph lacing
                    System.Diagnostics.Debug.Print("Xiph lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    break;
                case 4: // 00000100 = Fixed-size lacing
                    System.Diagnostics.Debug.Print("Fixed-size lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    for (int i = 1; i <= numberOfFrames; i++)
                        _stream.ReadByte(); // frames
                    break;
                case 6: // 00000110 = EMBL lacing
                    System.Diagnostics.Debug.Print("EBML lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    break;
            }

            // save subtitle data
            if (trackNumber == _subtitleRipTrackNumber)
            {
                long sublength = afterPosition - _stream.Position;
                if (sublength > 0)
                {
                    var buffer = new byte[sublength];
                    _stream.Read(buffer, 0, (int)sublength);

                    //string s = GetMatroskaString(sublength);
                    //s = s.Replace("\\N", Environment.NewLine);

                    _stream.Seek(afterPosition, SeekOrigin.Begin);
                    var duration = 0L;
                    elementId = ReadEbmlId();
                    if (elementId == ElementId.BlockDuration)
                    {
                        elementSize = (long)ReadVariableLengthUInt();
                        duration = (long)ReadUInt((int)elementSize);
                    }

                    _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));
                }
            }
        }

        public List<MatroskaSubtitleInfo> GetMatroskaSubtitleTracks()
        {
            // skip header
            _stream.Position = 4;
            var elementSize = (long)ReadVariableLengthUInt();
            _stream.Seek(elementSize, SeekOrigin.Current);

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(afterPosition);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks();
                        break;
                    case ElementId.Segment:
                        continue;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
            }

            return _subtitleList;
        }

        public List<SubtitleSequence> GetMatroskaSubtitle(int trackNumber, LoadMatroskaCallback callback)
        {
            _subtitleRipTrackNumber = trackNumber;

            // skip header
            _stream.Position = 4;
            var elementSize = (long)ReadVariableLengthUInt();
            _stream.Seek(elementSize, SeekOrigin.Current);

            ElementId elementId;
            while (_stream.Position < _streamLength && (elementId = ReadEbmlId()) != 0)
            {
                elementSize = (long)ReadVariableLengthUInt();
                var afterPosition = _stream.Position + elementSize;
                switch (elementId)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(afterPosition);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks();
                        break;
                    case ElementId.Cluster:
                        AnalyzeMatroskaCluster();
                        break;
                    case ElementId.Segment:
                        continue;
                }
                _stream.Seek(afterPosition, SeekOrigin.Begin);
                if (callback != null)
                    callback.Invoke(_stream.Position, _streamLength);
            }

            return _subtitleRip;
        }

        public void Dispose()
        {
            if (_stream != null)
            {
                _stream.Dispose();
            }
        }

        private ElementId ReadEbmlId()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_stream.ReadByte();
            var length = 0;

            // Begin by counting the bits unset before the highest set bit
            uint mask = 0x80;
            for (var i = 0; i < 8; i++)
            {
                // Start at left, shift to right
                if ((first & mask) == mask)
                {
                    length = i + 1;
                    break;
                }
                mask >>= 1;
            }
            if (length == 0)
            {
                // Invalid identifier
                return 0;
            }

            // Setup space to store the integer
            var data = new byte[length];
            data[0] = first;
            if (length > 1)
            {
                // Read the rest of the integer
                _stream.Read(data, 1, length - 1);
            }

            return (ElementId)BigEndianToUInt64(data);
        }

        private ulong ReadVariableLengthUInt()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_stream.ReadByte();
            var length = 0;

            // Begin by counting the bits unset before the highest set bit
            uint mask = 0x80;
            for (var i = 0; i < 8; i++)
            {
                // Start at left, shift to right
                if ((first & mask) == mask)
                {
                    length = i + 1;
                    break;
                }
                mask >>= 1;
            }
            if (length == 0)
            {
                return 0;
            }

            // Setup space to store the integer
            var data = new byte[length];
            data[0] = (byte)(first & (0xFF >> length));
            if (length > 1)
            {
                // Read the rest of the integer
                _stream.Read(data, 1, length - 1);
            }

            return BigEndianToUInt64(data);
        }

        /// <summary>
        /// Reads a fixed length unsigned integer from the current stream and advances the current
        /// position of the stream by the integer length in bytes.
        /// </summary>
        /// <param name="length">The length in bytes of the integer.</param>
        /// <returns>A 64-bit unsigned integer.</returns>
        private ulong ReadUInt(int length)
        {
            var data = new byte[length];
            _stream.Read(data, 0, length);
            return BigEndianToUInt64(data);
        }

        /// <summary>
        /// Reads a 2-byte signed integer from the current stream and advances the current position
        /// of the stream by two bytes.
        /// </summary>
        /// <returns>A 2-byte signed integer read from the current stream.</returns>
        private short ReadInt16()
        {
            var data = new byte[2];
            _stream.Read(data, 0, 2);
            return (short)(data[0] << 8 | data[1]);
        }

        /// <summary>
        /// Reads a 4-byte floating point value from the current stream and advances the current
        /// position of the stream by four bytes.
        /// </summary>
        /// <returns>A 4-byte floating point value read from the current stream.</returns>
        private unsafe float ReadFloat32()
        {
            var data = new byte[4];
            _stream.Read(data, 0, 4);
            var result = data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3];
            return *(float*)&result;
        }

        /// <summary>
        /// Reads a 8-byte floating point value from the current stream and advances the current
        /// position of the stream by eight bytes.
        /// </summary>
        /// <returns>A 8-byte floating point value read from the current stream.</returns>
        private unsafe double ReadFloat64()
        {
            var data = new byte[8];
            _stream.Read(data, 0, 8);
            var result = (long)(data[0] << 56 | data[1] << 48 | data[2] << 40 | data[3] << 32 | data[4] << 24 | data[5] << 16 | data[6] << 8 | data[7]);
            return *(double*)&result;
        }

        /// <summary>
        /// Returns a 64-bit unsigned integer converted from a big endian byte array.
        /// </summary>
        /// <param name="value">An array of bytes.</param>
        /// <returns>A 64-bit unsigned integer.</returns>
        private static ulong BigEndianToUInt64(byte[] value)
        {
            var result = 0UL;
            var shift = 0;
            for (var i = value.Length - 1; i >= 0; i--)
            {
                result |= (ulong)(value[i] << shift);
                shift += 8;
            }
            return result;
        }
    }
}
