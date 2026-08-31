using DfoServer.Game.Events.PcRoomTimePoint;

namespace DfoServer.Network.Parsers.Events
{
    internal static class PcRoomTimePointRequestParser
    {
        internal static bool TryParse(
            byte[] body,
            out PcRoomTimePointClaimCommand command)
        {
            command = null;
            if (body == null || body.Length < 2)
                return false;

            var selector = body[0];
            var index = body[1];
            if (selector == 0x00 && index == 0xFF)
            {
                command = new PcRoomTimePointClaimCommand
                {
                    Kind = PcRoomTimePointRequestKind.Query,
                    StageIndex = 0,
                    Selector = selector,
                    IndexOrFF = index,
                };
                return true;
            }

            if (index == 0xFF && TryMapDailySelector(selector, out var dailyStage))
            {
                command = new PcRoomTimePointClaimCommand
                {
                    Kind = PcRoomTimePointRequestKind.DailyReward,
                    StageIndex = dailyStage,
                    Selector = selector,
                    IndexOrFF = index,
                };
                return true;
            }

            if (selector == 0x10 && index <= 3)
            {
                command = new PcRoomTimePointClaimCommand
                {
                    Kind = PcRoomTimePointRequestKind.PeriodReward,
                    StageIndex = index + 1,
                    Selector = selector,
                    IndexOrFF = index,
                };
                return true;
            }

            return false;
        }

        private static bool TryMapDailySelector(byte selector, out int stageIndex)
        {
            switch (selector)
            {
                case 0x01:
                    stageIndex = 1;
                    return true;
                case 0x02:
                    stageIndex = 2;
                    return true;
                case 0x04:
                    stageIndex = 3;
                    return true;
                case 0x08:
                    stageIndex = 4;
                    return true;
                default:
                    stageIndex = 0;
                    return false;
            }
        }
    }
}
