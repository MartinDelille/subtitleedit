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

            Tracks = 0x1654AE6B,
            TrackEntry = 0xAE,

            Cluster = 0x1F43B675,
            Cues = 0x1C53BB6B,
            Attachments = 0x1941A469,
            Chapters = 0x1043A770,
            Tags = 0x1254C367
        }

        public delegate void LoadMatroskaCallback(long position, long total);

        private readonly string _fileName;
        private readonly FileStream _f;
        private readonly bool _valid;
        private int _pixelWidth, _pixelHeight;
        private double _frameRate;
        private string _videoCodecId;
        private double _durationInMilliseconds;

        private List<MatroskaSubtitleInfo> _subtitleList;
        private int _subtitleRipTrackNumber;
        private List<SubtitleSequence> _subtitleRip = new List<SubtitleSequence>();
        private long _timeCodeScale = 1000000; // Timestamp scale in nanoseconds (1.000.000 means all timestamps in the segment are expressed in milliseconds).
        private List<MatroskaTrackInfo> _tracks;

        public Matroska(string fileName)
        {
            _fileName = fileName;
            _f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
            
            _f.Position = 4;
            var dataSize = (long)ReadVariableLengthUInt();
            _f.Seek(dataSize, SeekOrigin.Current);

            var endOfFile = false;
            while (endOfFile == false)
            {
                var matroskaId = ReadEbmlId();
                if (matroskaId == 0)
                {
                    break;
                }
                dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == ElementId.Info)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaSegmentInformation(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Tracks)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTracks();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                    break;
                }
                else if (matroskaId != ElementId.Segment)
                {
                    _f.Seek(dataSize, SeekOrigin.Current);
                }

                endOfFile = _f.Position >= _f.Length;
            }
            return _tracks;
        }

        /// <summary>
        /// Get first time of track
        /// </summary>
        /// <param name="trackNumber">Track number</param>
        /// <returns>Start time in milliseconds</returns>
        public Int64 GetTrackStartTime(int trackNumber)
        {
            _tracks = new List<MatroskaTrackInfo>();
            
            _f.Position = 4;
            var dataSize = (long)ReadVariableLengthUInt();
            _f.Seek(dataSize, SeekOrigin.Current);

            var endOfFile = false;
            while (endOfFile == false)
            {
                var matroskaId = ReadEbmlId();
                if (matroskaId == 0)
                {
                    break;
                }
                dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == ElementId.Info)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaSegmentInformation(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Tracks)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTracks();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Cluster)
                {
                    afterPosition = _f.Position + dataSize;
                    return FindTrackStartInCluster(trackNumber);
                }
                else if (matroskaId != ElementId.Segment)
                {
                    _f.Seek(dataSize, SeekOrigin.Current);
                }

                endOfFile = _f.Position >= _f.Length;
            }
            return 0;
        }

        private Int64 FindTrackStartInCluster(int targetTrackNumber)
        {
            long clusterTimeCode = 0;
            int trackStartTime = -1;

            while (_f.Position < _f.Length)
            {
                var matroskaId = GetMatroskaClusterId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == 0xE7) // Timecode
                {
                    afterPosition = _f.Position + dataSize;
                    clusterTimeCode = (long)ReadUInt((int)dataSize); // Absolute timestamp of the cluster (based on TimecodeScale).
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xA0) // BlockGroup
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaBlock(clusterTimeCode);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xA3) // SimpleBlock
                {
                    afterPosition = _f.Position + dataSize;
                    var trackNumber = (int)ReadVariableLengthUInt();
                    if (trackNumber == targetTrackNumber)
                    {
                        trackStartTime = ReadInt16(); // Timecode (relative to Cluster timecode, signed int16)
                        break;
                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
            return (clusterTimeCode + trackStartTime) * _timeCodeScale / 1000000;
        }

        private UInt32 GetMatroskaTracksId()
        {
            byte b = (byte)_f.ReadByte();

            if (b == (uint)ElementId.Void ||
                b == (uint)ElementId.Crc32 ||
                b == (uint)ElementId.TrackEntry)
                return b;

            return 0;
        }

        private UInt32 GetMatroskaTrackEntryId()
        {
            UInt32 s = (UInt32)_f.ReadByte();

            if (s == (uint)ElementId.Void ||
                s == (uint)ElementId.Crc32 ||
                s == 0xD7 || // Track number
                s == 0x83 || // Track type
                s == 0xB9 || // Flag enabled
                s == 0x88 || // Flag default
                s == 0x9C || // Flag lacing
                s == 0x4F || // Track Time Code Scale
                s == 0xAA || // CodecDecodeAll
                s == 0xE0 || // Track Video
                s == 0xE1 || // Track Audio
                s == 0x86)   // Codec Id
            {
                return s;
            }

            s = s * 256 + (byte)_f.ReadByte();
            if (s == 0x73C5 || // TrackUID
                s == 0x55AA || // FlagForced
                s == 0x6DE7 || // MinCache
                s == 0x6DF8 || // MaxCache
                s == 0x55EE || // MaxBlockAdditionID
                s == 0x63A2 || // CodecPrivate
                s == 0x7446 || // AttachmentLink
                s == 0x6D80 || // ContentEncodings
                s == 0x537F || // TrackOffset
                s == 0x6FAB || // TrackOverlay
                s == 0x536E || // Name
                s == 0x6624 || // TrackTranslate
                s == 0x66FC || // TrackTranslateEditionUID
                s == 0x66BF || // TrackTranslateCodec
                s == 0x66A5)   // TrackTranslateTrackID
            {
                return s;
            }

            s = s * 256 + (byte)_f.ReadByte();
            if (s == 0x23E383 || // Default Duration
                s == 0x22B59C || // Language
                s == 0x258688 || // CodecName
                s == 0x23314F)   // TrackTimeCodeScale
                return s;

            return 0;
        }

        private UInt32 GetMatroskaTrackVideoId()
        {
            UInt32 s = (byte)_f.ReadByte();

            if (s == (uint)ElementId.Void ||
                s == (uint)ElementId.Crc32 ||
                s == 0xB0 || // PixelWidth
                s == 0xBA || // PixelHeight
                s == 0x9A)   // FlagInterlaced
            {
                return s;
            }

            s = s * 256 + (byte)_f.ReadByte();
            if (s == 0x54B0 || // DisplayWidth
                s == 0x54BA || // DisplayHeight
                s == 0x54BA || // DisplayHeight
                s == 0x54AA || // PixelCropButton
                s == 0x54BB || // PixelCropTop
                s == 0x54CC || // PixelCropLeft
                s == 0x54DD || // PixelCropRight
                s == 0x54DD || // PixelCropRight
                s == 0x54B2 || // DisplayUnit
                s == 0x54B3)   // AspectRatioType
                return s;
            s = s * 256 + (byte)_f.ReadByte();

            if (s == 0x2EB524)// ColourSpace
                return s;

            return 0;
        }

        private UInt32 GetMatroskaSegmentId()
        {
            byte b = (byte)_f.ReadByte();

            if (b == (uint)ElementId.Void || b == (uint)ElementId.Crc32)
                return b;

            UInt32 s = (UInt32)b * 256 + (byte)_f.ReadByte();
            if (s == 0x73A4 || // SegmentUID
                s == 0x7384 || // SegmentFilename
                s == 0x4444 || // SegmentFamily
                s == 0x6924 || // ChapterTranslate
                s == 0x69FC || // ChapterTranslateEditionUID
                s == 0x69BF || // ChapterTranslateCodec
                s == 0x69A5 || // ChapterTranslateID
                s == 0x4489 || // Duration
                s == 0x4461 || // DateUTC
                s == 0x7BA9 || // Title
                s == 0x4D80 || // MuxingApp
                s == 0x5741)   // WritingApp
            {
                return s;
            }

            s = (UInt32)b * 256 + (byte)_f.ReadByte();

            if (s == 0x3CB923 || // PrevUID
                s == 0x3C83AB || // PrevFilename
                s == 0x3EB923 || // NextUID
                s == 0x3E83BB || // NextFilename
                s == 0x2AD7B1)   // TimecodeScale
                return s;

            return 0;
        }

        private void AnalyzeMatroskaTrackVideo(long endPosition)
        {
            while (_f.Position < endPosition)
            {
                var matroskaId = GetMatroskaTrackVideoId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == 0xB0) // PixelWidth
                {
                    afterPosition = _f.Position + dataSize;
                    _pixelWidth = (int)ReadUInt((int)dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xBA) // PixelHeight
                {
                    afterPosition = _f.Position + dataSize;
                    _pixelHeight = (int)ReadUInt((int)dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
        }

        private string GetMatroskaString(long size)
        {
            try
            {
                byte[] buffer = new byte[size];
                _f.Read(buffer, 0, (int)size);
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
            string biCompression = string.Empty;
            int contentCompressionAlgorithm = -1;
            int contentEncodingType = -1;

            while (_f.Position < _f.Length)
            {
                var matroskaId = GetMatroskaTrackEntryId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == 0x23E383)// Default Duration
                {
                    afterPosition = _f.Position + dataSize;

                    defaultDuration = (int)ReadUInt((int)dataSize);

                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xE0)// Video settings
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTrackVideo(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                    isVideo = true;
                }
                else if (matroskaId == 0xE1)// Audio settings
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTrackVideo(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                    isAudio = true;
                }
                else if (matroskaId == 0xD7) // Track number
                {
                    afterPosition = _f.Position + dataSize;
                    if (dataSize == 1)
                    {
                        trackNumber = (byte)_f.ReadByte();
                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x536E) // Name
                {
                    afterPosition = _f.Position + dataSize;
                    name = GetMatroskaString(dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x22B59C) // Language
                {
                    afterPosition = _f.Position + dataSize;
                    language = GetMatroskaString(dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x86) // CodecId
                {
                    afterPosition = _f.Position + dataSize;
                    codecId = GetMatroskaString(dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x83) // Track type
                {
                    afterPosition = _f.Position + dataSize;
                    if (dataSize == 1)
                    {
                        byte trackType = (byte)_f.ReadByte();
                        if (trackType == 0x11) // subtitle
                            isSubtitle = true;
                        if (trackType == 1)
                            isVideo = true;
                        if (trackType == 2)
                            isAudio = true;
                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x63A2) // CodecPrivate
                {
                    afterPosition = _f.Position + dataSize;
                    codecPrivate = GetMatroskaString(dataSize);
                    if (codecPrivate.Length > 20)
                        biCompression = codecPrivate.Substring(16, 4);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x6D80) // ContentEncodings
                {
                    afterPosition = _f.Position + dataSize;

                    contentCompressionAlgorithm = 0; // default value
                    contentEncodingType = 0; // default value

                    int contentEncoding1 = _f.ReadByte();
                    int contentEncoding2 = _f.ReadByte();

                    if (contentEncoding1 == 0x62 && contentEncoding2 == 0x40)
                    {
                        AnalyzeMatroskaContentEncoding(afterPosition, ref contentCompressionAlgorithm, ref contentEncodingType);
                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
            if (_tracks != null)
            {
                _tracks.Add(new MatroskaTrackInfo()
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
            while (_f.Position < endPosition)
            {
                int ebmlId = _f.ReadByte() * 256 + _f.ReadByte();
                if (ebmlId == 0)
                {
                    break;
                }

                if (ebmlId == 0x5031)// ContentEncodingOrder
                {
                    int contentEncodingOrder = _f.ReadByte() * 256 + _f.ReadByte();
                    System.Diagnostics.Debug.WriteLine("ContentEncodingOrder: " + contentEncodingOrder);
                }
                else if (ebmlId == 0x5032)// ContentEncodingScope
                {
                    int contentEncodingScope = _f.ReadByte() * 256 + _f.ReadByte();
                    System.Diagnostics.Debug.WriteLine("ContentEncodingScope: " + contentEncodingScope);
                }
                else if (ebmlId == 0x5033)// ContentEncodingType
                {
                    contentEncodingType = _f.ReadByte() * 256 + _f.ReadByte();
                }
                else if (ebmlId == 0x5034)// ContentCompression
                {
                    var dataSize = (long)ReadVariableLengthUInt();
                    long afterPosition = _f.Position + dataSize;
                    while (_f.Position < afterPosition)
                    {
                        int contentCompressionId = _f.ReadByte() * 256 + _f.ReadByte();
                        if (contentCompressionId == 0x4254)
                        {
                            contentCompressionAlgorithm = _f.ReadByte() * 256 + _f.ReadByte();
                        }
                        else if (contentCompressionId == 0x4255)
                        {
                            int contentCompSettings = _f.ReadByte() * 256 + _f.ReadByte();
                            System.Diagnostics.Debug.WriteLine("contentCompSettings: " + contentCompSettings);
                        }

                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
            }
        }

        private void AnalyzeMatroskaSegmentInformation(long endPosition)
        {
            long timeCodeScale = 0;
            double duration8b = 0;

            while (_f.Position < endPosition)
            {
                var matroskaId = GetMatroskaSegmentId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == 0x2AD7B1)// TimecodeScale - u-integer     Timecode scale in nanoseconds (1.000.000 means all timecodes in the segment are expressed in milliseconds).
                {
                    afterPosition = _f.Position + dataSize;
                    _timeCodeScale = (int)ReadUInt((int)dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0x4489)// Duration (float)
                {
                    afterPosition = _f.Position + dataSize;
                    duration8b = dataSize == 4 ? ReadFloat32() : ReadFloat64();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
            if (timeCodeScale > 0 && duration8b > 0)
                _durationInMilliseconds = duration8b / timeCodeScale * 1000000.0;
            else if (duration8b > 0)
                _durationInMilliseconds = duration8b;
        }

        private void AnalyzeMatroskaTracks()
        {
            _subtitleList = new List<MatroskaSubtitleInfo>();

            while (_f.Position < _f.Length)
            {
                var matroskaId = GetMatroskaTracksId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                if (matroskaId == (uint)ElementId.TrackEntry)
                {
                    var afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTrackEntry();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
        }

        public void GetMatroskaInfo(out bool hasConstantFrameRate, out double frameRate, out int pixelWidth, out int pixelHeight, out double millisecondDuration, out string videoCodec)
        {
            _durationInMilliseconds = 0;

            _f.Position = 4;
            var dataSize = (long)ReadVariableLengthUInt();
            _f.Seek(dataSize, SeekOrigin.Current);

            var endOfFile = false;
            while (endOfFile == false)
            {
                var matroskaId = ReadEbmlId();
                if (matroskaId == 0)
                {
                    break;
                }
                dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == ElementId.Info)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaSegmentInformation(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Tracks)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTracks();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                    break;
                }
                else if (matroskaId == ElementId.Cluster)
                {
                    afterPosition = _f.Position + dataSize;
                    //if (f.Position > 8000000)
                    //    System.Windows.Forms.MessageBox.Show("8mb");
                    AnalyzeMatroskaCluster();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId != ElementId.Segment)
                {
                    _f.Seek(dataSize, SeekOrigin.Current);
                }

                endOfFile = _f.Position >= _f.Length;
            }

            pixelWidth = _pixelWidth;
            pixelHeight = _pixelHeight;
            frameRate = _frameRate;
            hasConstantFrameRate = _frameRate > 0;
            millisecondDuration = _durationInMilliseconds;
            videoCodec = _videoCodecId;
        }

        private UInt32 GetMatroskaClusterId()
        {
            UInt32 s = (byte)_f.ReadByte();

            if (s == 0xE7 || // TimeCode
                s == 0xA7 || // Position
                s == 0xAB || // PrevSize
                s == 0xA0 || // BlockGroup
                s == 0xA1 || // Block
                s == 0xA2 || // BlockVirtual
                s == 0xA6 || // BlockMore
                s == 0xEE || // BlockAddID
                s == 0xA5 || // BlockAdditional
                s == 0x9B || // BlockDuration
                s == 0xFA || // ReferencePriority
                s == 0xFB || // ReferenceBlock
                s == 0xFD || // ReferenceVirtual
                s == 0xA4 || // CodecState
                s == 0x8E || // Slices
                s == 0x8E || // TimeSlice
                s == 0xCC || // LaceNumber
                s == 0xCD || // FrameNumber
                s == 0xCB || // BlockAdditionID
                s == 0xCE || // Delay
                s == 0xCF || // Duration
                s == 0xA3)   // SimpleBlock
            {
                return s;
            }

            s = s * 256 + (byte)_f.ReadByte();

            if (s == 0x5854 || // SilentTracks
                s == 0x58D7 || // SilentTrackNumber
                s == 0x75A1) // BlockAdditions
                return s;

            return 0;
        }

        private void AnalyzeMatroskaCluster()
        {
            long clusterTimeCode = 0;
            const long duration = 0;

            while (_f.Position < _f.Length)
            {
                var matroskaId = GetMatroskaClusterId();
                if (matroskaId == 0)
                {
                    break;
                }
                var dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == 0xE7) // Timecode
                {
                    afterPosition = _f.Position + dataSize;
                    clusterTimeCode = (long)ReadUInt((int)dataSize);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xA0) // BlockGroup
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaBlock(clusterTimeCode);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == 0xA3) // SimpleBlock
                {
                    afterPosition = _f.Position + dataSize;
                    long before = _f.Position;
                    var trackNumber = (int)ReadVariableLengthUInt();
                    if (trackNumber == _subtitleRipTrackNumber)
                    {
                        int timeCode = ReadInt16();

                        // lacing
                        byte flags = (byte)_f.ReadByte();
                        byte numberOfFrames = 0;
                        switch ((flags & 6))  // 6 = 00000110
                        {
                            case 0: System.Diagnostics.Debug.Print("No lacing");   // No lacing
                                break;
                            case 2: System.Diagnostics.Debug.Print("Xiph lacing"); // 2 = 00000010 = Xiph lacing
                                numberOfFrames = (byte)_f.ReadByte();
                                numberOfFrames++;
                                break;
                            case 4: System.Diagnostics.Debug.Print("fixed-size");  // 4 = 00000100 = Fixed-size lacing
                                numberOfFrames = (byte)_f.ReadByte();
                                numberOfFrames++;
                                for (int i = 1; i <= numberOfFrames; i++)
                                    _f.ReadByte(); // frames
                                break;
                            case 6: System.Diagnostics.Debug.Print("EBML");        // 6 = 00000110 = EMBL
                                numberOfFrames = (byte)_f.ReadByte();
                                numberOfFrames++;
                                break;
                        }

                        byte[] buffer = new byte[dataSize - (_f.Position - before)];
                        _f.Read(buffer, 0, buffer.Length);
                        _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));

                    }
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else
                    _f.Seek(dataSize, SeekOrigin.Current);
            }
        }

        private void AnalyzeMatroskaBlock(long clusterTimeCode)
        {
            long duration = 0;
            byte b = (byte)_f.ReadByte();
            if (b == 0xA1) // Block
            {
                var dataSize = (long)ReadVariableLengthUInt();
                long afterPosition = _f.Position + dataSize;

                // track number
                var trackNo = (int)ReadVariableLengthUInt();

                // time code
                var timeCode = ReadInt16();

                // lacing
                byte flags = (byte)_f.ReadByte();
                byte numberOfFrames = 0;
                switch ((flags & 6))  // 6 = 00000110
                {
                    case 0: System.Diagnostics.Debug.Print("No lacing");   // No lacing
                        break;
                    case 2: System.Diagnostics.Debug.Print("Xiph lacing"); // 2 = 00000010 = Xiph lacing
                        numberOfFrames = (byte)_f.ReadByte();
                        numberOfFrames++;
                        break;
                    case 4: System.Diagnostics.Debug.Print("fixed-size");  // 4 = 00000100 = Fixed-size lacing
                        numberOfFrames = (byte)_f.ReadByte();
                        numberOfFrames++;
                        for (int i = 1; i <= numberOfFrames; i++)
                            _f.ReadByte(); // frames
                        break;
                    case 6: System.Diagnostics.Debug.Print("EBML");        // 6 = 00000110 = EMBL
                        numberOfFrames = (byte)_f.ReadByte();
                        numberOfFrames++;
                        break;
                }

                // save subtitle data
                if (trackNo == _subtitleRipTrackNumber)
                {
                    long sublength = afterPosition - _f.Position;
                    if (sublength > 0)
                    {
                        byte[] buffer = new byte[sublength];
                        _f.Read(buffer, 0, (int)sublength);

                        //string s = GetMatroskaString(sublength);
                        //s = s.Replace("\\N", Environment.NewLine);

                        _f.Seek(afterPosition, SeekOrigin.Begin);
                        b = (byte)_f.ReadByte();
                        if (b == 0x9B) // BlockDuration
                        {
                            dataSize = (long)ReadVariableLengthUInt();
                            duration = (long)ReadUInt((int)dataSize);
                        }

                        _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));
                    }
                }

            }
        }

        public List<MatroskaSubtitleInfo> GetMatroskaSubtitleTracks()
        {
            _f.Position = 4;
            var dataSize = (long)ReadVariableLengthUInt();
            _f.Seek(dataSize, SeekOrigin.Current);

            var endOfFile = false;
            while (endOfFile == false)
            {
                var matroskaId = ReadEbmlId();
                if (matroskaId == 0)
                {
                    break;
                }
                dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == ElementId.Info)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaSegmentInformation(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Tracks)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTracks();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                    break;
                }
                else if (matroskaId != ElementId.Segment)
                {
                    _f.Seek(dataSize, SeekOrigin.Current);
                }

                endOfFile = _f.Position >= _f.Length;
            }

            return _subtitleList;
        }

        public List<SubtitleSequence> GetMatroskaSubtitle(int trackNumber, LoadMatroskaCallback callback)
        {
            _subtitleRipTrackNumber = trackNumber;

            _f.Position = 4;
            var dataSize = (long)ReadVariableLengthUInt();
            _f.Seek(dataSize, SeekOrigin.Current);

            var endOfFile = false;
            while (endOfFile == false)
            {
                var matroskaId = ReadEbmlId();
                if (matroskaId == 0)
                {
                    break;
                }
                dataSize = (long)ReadVariableLengthUInt();

                long afterPosition;
                if (matroskaId == ElementId.Info)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaSegmentInformation(afterPosition);
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Tracks)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaTracks();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId == ElementId.Cluster)
                {
                    afterPosition = _f.Position + dataSize;
                    AnalyzeMatroskaCluster();
                    _f.Seek(afterPosition, SeekOrigin.Begin);
                }
                else if (matroskaId != ElementId.Segment)
                {
                    _f.Seek(dataSize, SeekOrigin.Current);
                }
                if (callback != null)
                    callback.Invoke(_f.Position, _f.Length);
                endOfFile = _f.Position >= _f.Length;
            }

            return _subtitleRip;
        }

        public void Dispose()
        {
            if (_f != null)
            {
                _f.Dispose();
            }
        }

        private ElementId ReadEbmlId()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_f.ReadByte();
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
                _f.Read(data, 1, length - 1);
            }

            return (ElementId)BigEndianToUInt64(data);
        }

        private ulong ReadVariableLengthUInt()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_f.ReadByte();
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
                _f.Read(data, 1, length - 1);
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
            _f.Read(data, 0, length);
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
            _f.Read(data, 0, 2);
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
            _f.Read(data, 0, 4);
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
            _f.Read(data, 0, 8);
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
