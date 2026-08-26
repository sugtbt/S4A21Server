using System;

namespace DfoServer.Game.Inventory
{
    /// A21 CMD RENT_EQUIPMENT_ITEM 请求。
    /// 客户端固定发送 22B；IDA 证据表明真正的装备模板 ID 位于非对齐 offset 14，
    /// offset 18 是客户端上下文值，前 14B 是未初始化的临时缓冲区，不能按旧布局解析。
    public static class RentalWeaponRequestCodec
    {
        public const int RequestBodySize = 22;
        public const int ItemTemplateOffset = 14;
        public const int ClientContextOffset = 18;
        public const int RentalDurationSeconds = 86400;

        public static bool TryParse(
            byte[] body,
            out uint inventoryTemplateId,
            out uint clientContext,
            out int starCost)
        {
            inventoryTemplateId = 0;
            clientContext = 0;
            starCost = 0;
            if (body == null || body.Length != RequestBodySize)
                return false;

            if (!TryReadUInt32(body, ItemTemplateOffset, out inventoryTemplateId)
                || !TryReadUInt32(body, ClientContextOffset, out clientContext))
                return false;

            if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate((int)inventoryTemplateId))
                return false;

            starCost = RentalWeaponInventoryMapper.GetStarPrice((int)inventoryTemplateId);
            return starCost > 0;
        }

        public static string DescribeParseFailure(byte[] body)
        {
            if (body == null)
                return "body=null";

            if (body.Length != RequestBodySize)
                return $"bodyLen={body.Length}!=expected={RequestBodySize}";

            var inventory = TryReadUInt32(body, ItemTemplateOffset, out var inventoryTemplateId) ? $"0x{inventoryTemplateId:X8}" : "n/a";
            var context = TryReadUInt32(body, ClientContextOffset, out var clientContext) ? $"0x{clientContext:X8}" : "n/a";
            var inventoryValid = TryReadUInt32(body, ItemTemplateOffset, out inventoryTemplateId)
                && RentalWeaponInventoryMapper.IsValidInventoryTemplate((int)inventoryTemplateId);
            var starCost = inventoryValid
                ? RentalWeaponInventoryMapper.GetStarPrice((int)inventoryTemplateId)
                : 0;

            return $"inv={inventory} invValid={inventoryValid} catalogStarCost={starCost} clientContext={context}";
        }

        private static bool TryReadUInt32(byte[] body, int offset, out uint value)
        {
            value = 0;
            if (body == null || offset < 0 || offset + 4 > body.Length)
                return false;

            value = BitConverter.ToUInt32(body, offset);
            return true;
        }

    }
}
