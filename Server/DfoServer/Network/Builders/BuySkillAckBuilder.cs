using DfoServer.Game.Skills;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    public static class BuySkillAckBuilder
    {
        public static byte[] Build(BuySkillResult result)
        {
            var w = new GamePacketWriter();
            if (result == null || !result.Success)
            {
                w.WriteByte(0x00);                                  
                w.WriteByte(result != null ? result.ErrorCode : (byte)1);
                return w.ToArray();
            }

            w.WriteByte(0x01);                                      
            w.WriteByte(result.SkillTree);                         
            w.WriteUInt16(result.RemainSp);                        
            w.WriteUInt16(result.RemainTp);
            w.WriteByte((byte)result.Entries.Count);               
            foreach (var e in result.Entries)
            {
                w.WriteByte(e.Slot);                               
                w.WriteUInt16(e.SkillId);                          
                w.WriteByte(e.Level);                              
                if (e.HasCmd && e.CommandBytes.Count > 0)
                {
                    w.WriteByte(0x01);
                    var commandCount = e.CommandBytes.Count > byte.MaxValue
                        ? byte.MaxValue
                        : e.CommandBytes.Count;
                    w.WriteByte((byte)commandCount);
                    for (var index = 0; index < commandCount; index++)
                        w.WriteByte(e.CommandBytes[index]);
                }
                else
                {
                    w.WriteByte(0x00);
                }
            }
            return w.ToArray();
        }
    }
}
