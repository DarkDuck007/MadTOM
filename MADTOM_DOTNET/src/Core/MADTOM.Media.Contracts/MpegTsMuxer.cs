using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MADTOM.Media.Contracts;

public enum VideoCodecType
{
    H264,
    Hevc
}

/// <summary>
/// Minimal MPEG-2 Transport Stream (MPEG-TS) muxer in C#.
/// Packetizes H.264 / HEVC video frames into standard 188-byte TS packets.
/// </summary>
public sealed class MpegTsMuxer
{
    public const int TsPacketSize = 188;
    public const ushort PatPid = 0x0000;
    public const ushort PmtPid = 0x0100;
    public const ushort VideoPid = 0x0101;
    public const byte StreamTypeH264 = 0x1B;
    public const byte StreamTypeHevc = 0x24;

    private readonly VideoCodecType _codec;
    private byte _patContinuity;
    private byte _pmtContinuity;
    private byte _videoContinuity;
    private bool _patPmtEmitted;

    public MpegTsMuxer(VideoCodecType codec = VideoCodecType.H264)
    {
        _codec = codec;
    }

    /// <summary>
    /// Packetizes an encoded video access unit (with NAL units) into MPEG-TS packets.
    /// pts90Khz: Presentation Time Stamp in 90kHz units.
    /// isKeyframe: If true, PAT and PMT are prepended and random_access_indicator is set.
    /// </summary>
    public byte[] MuxAccessUnit(ReadOnlySpan<byte> accessUnitData, long pts90Khz, bool isKeyframe)
    {
        using var ms = new MemoryStream();

        // Emit PAT and PMT on keyframes or on initial frame
        if (isKeyframe || !_patPmtEmitted)
        {
            WritePat(ms);
            WritePmt(ms);
            _patPmtEmitted = true;
        }

        WritePesVideo(ms, accessUnitData, pts90Khz, isKeyframe);
        return ms.ToArray();
    }

    private void WritePat(MemoryStream ms)
    {
        Span<byte> packet = stackalloc byte[TsPacketSize];
        packet.Fill(0xFF);

        // Header: 4 bytes
        packet[0] = 0x47; // Sync byte
        packet[1] = 0x40; // Payload unit start indicator, PID MSB (0)
        packet[2] = 0x00; // PID LSB (0x00)
        packet[3] = (byte)(0x10 | (_patContinuity++ & 0x0F)); // Payload only

        // Pointer field
        packet[4] = 0x00;

        // PAT Table syntax
        packet[5] = 0x00; // table_id = 0 (PAT)
        packet[6] = 0xB0; // section_syntax_indicator = 1, '0', reserved, section_length MSB
        packet[7] = 0x0D; // section_length = 13 bytes
        packet[8] = 0x00; // transport_stream_id MSB
        packet[9] = 0x01; // transport_stream_id LSB
        packet[10] = 0xC1; // reserved, version_number = 0, current_next_indicator = 1
        packet[11] = 0x00; // section_number = 0
        packet[12] = 0x00; // last_section_number = 0

        // Program 1 -> PMT PID 0x0100
        packet[13] = 0x00;
        packet[14] = 0x01; // Program 1
        packet[15] = 0xE1; // reserved '111', PMT PID MSB (0x01)
        packet[16] = 0x00; // PMT PID LSB (0x00)

        // CRC32 over bytes 5..16
        uint crc = CalculateCrc32(packet.Slice(5, 12));
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(17, 4), crc);

        ms.Write(packet);
    }

    private void WritePmt(MemoryStream ms)
    {
        Span<byte> packet = stackalloc byte[TsPacketSize];
        packet.Fill(0xFF);

        // Header
        packet[0] = 0x47; // Sync byte
        packet[1] = (byte)(0x40 | ((PmtPid >> 8) & 0x1F)); // Payload unit start
        packet[2] = (byte)(PmtPid & 0xFF);
        packet[3] = (byte)(0x10 | (_pmtContinuity++ & 0x0F));

        // Pointer field
        packet[4] = 0x00;

        // PMT Table syntax
        packet[5] = 0x02; // table_id = 2 (PMT)
        packet[6] = 0xB0; // section_syntax_indicator = 1, reserved, section_length MSB
        packet[7] = 0x12; // section_length = 18 bytes
        packet[8] = 0x00; // program_number MSB
        packet[9] = 0x01; // program_number LSB = 1
        packet[10] = 0xC1; // reserved, version = 0, current_next = 1
        packet[11] = 0x00; // section_number
        packet[12] = 0x00; // last_section_number
        packet[13] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)); // PCR PID MSB
        packet[14] = (byte)(VideoPid & 0xFF);                 // PCR PID LSB
        packet[15] = 0xF0; // reserved, program_info_length MSB (0)
        packet[16] = 0x00; // program_info_length LSB (0)

        // Stream descriptor: Video
        packet[17] = _codec == VideoCodecType.Hevc ? StreamTypeHevc : StreamTypeH264;
        packet[18] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F));
        packet[19] = (byte)(VideoPid & 0xFF);
        packet[20] = 0xF0; // reserved, ES_info_length MSB (0)
        packet[21] = 0x00; // ES_info_length LSB (0)

        // CRC32 over bytes 5..21
        uint crc = CalculateCrc32(packet.Slice(5, 17));
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(22, 4), crc);

        ms.Write(packet);
    }

    private void WritePesVideo(MemoryStream ms, ReadOnlySpan<byte> naluData, long pts90Khz, bool isKeyframe)
    {
        // Build PES packet header
        // Prefix: 00 00 01 E0
        // Length: 0 (unbounded for video)
        // Flags: 10 (MPEG-2 PES), PTS_DTS_flags = 10 (PTS only)
        // PES header data length = 5 (PTS)
        Span<byte> pesHeader = stackalloc byte[14];
        pesHeader[0] = 0x00;
        pesHeader[1] = 0x00;
        pesHeader[2] = 0x01;
        pesHeader[3] = 0xE0; // video stream 0
        pesHeader[4] = 0x00; // PES packet length (0 for video)
        pesHeader[5] = 0x00;
        pesHeader[6] = 0x80; // 10xxxxxx
        pesHeader[7] = 0x80; // PTS only (10xxxxxx)
        pesHeader[8] = 0x05; // PES_header_data_length = 5

        // Encode 33-bit PTS into 5 bytes:
        // 0010 PTS[32..30] 1 | PTS[29..15] 1 | PTS[14..0] 1
        pesHeader[9] = (byte)(0x21 | (((pts90Khz >> 30) & 0x07) << 1));
        pesHeader[10] = (byte)((pts90Khz >> 22) & 0xFF);
        pesHeader[11] = (byte)(0x01 | (((pts90Khz >> 15) & 0x7F) << 1));
        pesHeader[12] = (byte)((pts90Khz >> 7) & 0xFF);
        pesHeader[13] = (byte)(0x01 | ((pts90Khz & 0x7F) << 1));

        // Combined stream: PES header + NAL units
        int totalPayloadLen = pesHeader.Length + naluData.Length;
        int bytesWritten = 0;
        bool isFirstPacket = true;

        Span<byte> packet = stackalloc byte[TsPacketSize];

        while (bytesWritten < totalPayloadLen)
        {
            packet.Fill(0xFF);
            packet[0] = 0x47; // Sync byte

            byte pidMsb = (byte)((VideoPid >> 8) & 0x1F);
            if (isFirstPacket)
            {
                pidMsb |= 0x40; // Payload unit start indicator
            }
            packet[1] = pidMsb;
            packet[2] = (byte)(VideoPid & 0xFF);

            int remaining = totalPayloadLen - bytesWritten;

            if (isFirstPacket)
            {
                // First packet includes Adaptation Field with PCR and random access indicator if keyframe
                // Adaptation field length: 7 bytes (flags + 6 bytes PCR)
                packet[3] = (byte)(0x30 | (_videoContinuity++ & 0x0F)); // AF + Payload
                packet[4] = 0x07; // AF length
                packet[5] = (byte)(isKeyframe ? 0x50 : 0x10); // PCR flag (+ random_access_indicator if keyframe)

                // Write 48-bit PCR (base 33 bits + reserved 6 + ext 9)
                long pcrBase = pts90Khz;
                packet[6] = (byte)((pcrBase >> 25) & 0xFF);
                packet[7] = (byte)((pcrBase >> 17) & 0xFF);
                packet[8] = (byte)((pcrBase >> 9) & 0xFF);
                packet[9] = (byte)((pcrBase >> 1) & 0xFF);
                packet[10] = (byte)(((pcrBase & 0x01) << 7) | 0x7E);
                packet[11] = 0x00;

                int payloadOffset = 12;
                int maxPayloadInPacket = TsPacketSize - payloadOffset;
                int toCopy = Math.Min(remaining, maxPayloadInPacket);

                CopyPesAndNalu(pesHeader, naluData, bytesWritten, packet.Slice(payloadOffset, toCopy));
                bytesWritten += toCopy;
                isFirstPacket = false;
            }
            else
            {
                // Subsequent packets
                int maxPayload = TsPacketSize - 4; // 184 bytes
                if (remaining < maxPayload)
                {
                    // Need Adaptation Field for padding / stuffing
                    int stuffingLen = maxPayload - remaining;
                    packet[3] = (byte)(0x30 | (_videoContinuity++ & 0x0F)); // AF + Payload
                    packet[4] = (byte)(stuffingLen - 1); // AF length
                    if (stuffingLen > 1)
                    {
                        packet[5] = 0x00; // flags
                        // packet[6..] already filled with 0xFF stuffing
                    }

                    int payloadOffset = 4 + stuffingLen;
                    CopyPesAndNalu(pesHeader, naluData, bytesWritten, packet.Slice(payloadOffset, remaining));
                    bytesWritten += remaining;
                }
                else
                {
                    // Full payload packet
                    packet[3] = (byte)(0x10 | (_videoContinuity++ & 0x0F)); // Payload only
                    CopyPesAndNalu(pesHeader, naluData, bytesWritten, packet.Slice(4, maxPayload));
                    bytesWritten += maxPayload;
                }
            }

            ms.Write(packet);
        }
    }

    private static void CopyPesAndNalu(ReadOnlySpan<byte> pesHeader, ReadOnlySpan<byte> naluData, int sourceOffset, Span<byte> destination)
    {
        int destPos = 0;
        int destLen = destination.Length;

        // If sourceOffset < pesHeader.Length, copy from pesHeader first
        if (sourceOffset < pesHeader.Length)
        {
            int pesToCopy = Math.Min(pesHeader.Length - sourceOffset, destLen);
            pesHeader.Slice(sourceOffset, pesToCopy).CopyTo(destination.Slice(destPos, pesToCopy));
            destPos += pesToCopy;
            sourceOffset += pesToCopy;
        }

        // Remaining bytes copy from naluData
        if (destPos < destLen)
        {
            int naluOffset = sourceOffset - pesHeader.Length;
            int naluToCopy = destLen - destPos;
            naluData.Slice(naluOffset, naluToCopy).CopyTo(destination.Slice(destPos, naluToCopy));
        }
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
            {
                uint bit = (uint)((b >> i) & 1);
                uint c7 = (crc >> 31) & 1;
                crc <<= 1;
                if ((c7 ^ bit) != 0)
                {
                    crc ^= 0x04C11DB7; // MPEG-2 TS polynomial
                }
            }
        }
        return crc;
    }
}
