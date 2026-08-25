using DfoServer.Infrastructure;
using System;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    internal sealed class CreatureDetail
    {
        private byte[] _nameBytes = Array.Empty<byte>();

        public int Uid { get; set; }

        public int CreatureKey
        {
            get => Uid;
            set => Uid = value;
        }

        public string Name
        {
            get => _nameBytes.Length == 0 ? string.Empty : ClientTextEncoding.GetString(_nameBytes);
            set => _nameBytes = string.IsNullOrEmpty(value) ? Array.Empty<byte>() : ClientTextEncoding.GetBytes(value);
        }

        public byte[] NameBytes
        {
            get => Copy(_nameBytes);
            set => _nameBytes = Copy(value);
        }

        public byte Field04 { get; set; }

        public byte Stomach
        {
            get => Field04;
            set => Field04 = value;
        }

        public byte ModeFlag { get; set; }

        public byte Mode1Field0A { get; set; }

        public byte Mode1Field0B { get; set; }

        public int ProgressValue32 { get; set; }

        public int Exp
        {
            get => ProgressValue32;
            set => ProgressValue32 = value;
        }

        public int FieldAfterValue32 { get; set; }

        public int Level
        {
            get => FieldAfterValue32;
            set => FieldAfterValue32 = value;
        }

        public int ExpireDate { get; set; }

        public byte TailFlag { get; set; }

        public static int GetExpireDate(int itemId)
        {
            if (itemId <= 0)
                return 0;

            try
            {
                if (ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment)
                    && EquipmentExpirationPolicyResolver.TryResolve(equipment, out var equipmentPolicy))
                {
                    if (equipmentPolicy.UsablePeriodDays > 0)
                        return PvfExpirationMetadata.AddDaysFromNow(equipmentPolicy.UsablePeriodDays);

                    return equipmentPolicy.AbsoluteExpirationUnixTime;
                }

                if (ItemMetadataResolver.TryLoadStackableFile(itemId, out var stackable)
                    && StackableExpirationPolicyResolver.TryResolve(stackable, out var stackablePolicy))
                {
                    if (stackablePolicy.UsablePeriodDays > 0)
                        return PvfExpirationMetadata.AddDaysFromNow(stackablePolicy.UsablePeriodDays);

                    return stackablePolicy.AbsoluteExpirationUnixTime;
                }
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        public static int GetStaticRemainDate(int itemId)
        {
            if (itemId <= 0)
                return 0;

            try
            {
                if (ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment)
                    && TryParsePetExpirationRemainSeconds(
                        equipment.GetStringValue("expiration date"),
                        out var remainingSeconds))
                    return remainingSeconds;
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        public static int GetRemainDate(int expireDate)
        {
            if (expireDate <= 0)
                return 0;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var remain = expireDate - now;
            return remain <= 0 ? 0 : remain > int.MaxValue ? int.MaxValue : (int)remain;
        }

        public int GetRemainDate()
        {
            return GetRemainDate(ExpireDate);
        }

        private static bool TryParsePetExpirationRemainSeconds(string rawValue, out int remainingSeconds)
        {
            remainingSeconds = 0;
            var normalized = (rawValue ?? string.Empty).Trim().Trim('`').Trim();
            if (normalized.Length == 0)
                return false;

            if (int.TryParse(
                    normalized,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var numericValue))
            {
                if (numericValue <= 0)
                    return true;

                if (numericValue < 1_000_000_000)
                {
                    remainingSeconds = numericValue;
                    return true;
                }
            }

            if (!PvfExpirationMetadata.TryParseUnixTime(normalized, out var expireDate))
                return false;

            remainingSeconds = GetRemainDate(expireDate);
            return true;
        }

        public byte GetAliveState()
        {
            return Stomach > 0 ? (byte)1 : (byte)0;
        }

        private static byte[] Copy(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            var result = new byte[data.Length];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            return result;
        }
    }
}
