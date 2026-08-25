using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Names
{
    public enum NameInputValidationFailure
    {
        None,
        Null,
        TooShort,
        TooLong,
        InvalidEncoding,
        DisallowedUnicodeRange,
        Slang,
        DisallowedCharacter,
    }

    public static class NameInputValidator
    {
        public const byte InvalidNameErrorCode = 0x9F;

        public static bool TryValidateRawName(
            byte[] nameBytes,
            int minBytes,
            int maxBytes,
            out string text,
            out NameInputValidationFailure failure)
        {
            text = string.Empty;

            if (nameBytes == null)
            {
                failure = NameInputValidationFailure.Null;
                return false;
            }

            if (nameBytes.Length < minBytes)
            {
                failure = NameInputValidationFailure.TooShort;
                return false;
            }

            if (nameBytes.Length > maxBytes)
            {
                failure = NameInputValidationFailure.TooLong;
                return false;
            }

            if (nameBytes.Length == 0)
            {
                failure = NameInputValidationFailure.None;
                return true;
            }

            if (!ClientTextEncoding.TryGetStringStrict(nameBytes, out text))
            {
                failure = NameInputValidationFailure.InvalidEncoding;
                return false;
            }

            var rules = NameInputRuleSet.Current;
            if (!rules.IsAllowedByUnicodeRange(text))
            {
                failure = NameInputValidationFailure.DisallowedUnicodeRange;
                return false;
            }

            if (rules.HasSlang(text))
            {
                failure = NameInputValidationFailure.Slang;
                return false;
            }

            if (rules.HasSpecialCharacter(nameBytes, text))
            {
                failure = NameInputValidationFailure.DisallowedCharacter;
                return false;
            }

            failure = NameInputValidationFailure.None;
            return true;
        }
    }
}
