using DfoServer.Game.DailyReset;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class FixedDailyTicketSelfTest
    {
        public static int Run()
        {
            const int ticketId = 4183;
            var appended = PvfDailyRefillItemProvider.AddFixedDailyTicketRule(
                Array.Empty<DailyRefillItemRule>());
            var ticket = appended.SingleOrDefault(rule => rule.ItemId == ticketId);

            var existing = PvfDailyRefillItemProvider.AddFixedDailyTicketRule(
                new List<DailyRefillItemRule>
                {
                    new DailyRefillItemRule
                    {
                        ItemId = ticketId,
                        Quantity = 3,
                        ExpirationBeijing = DateTime.MaxValue,
                        Mode = DailyRefillMode.AddUpToStackLimit,
                    },
                });

            var unlimitedRule = new DailyRefillItemRule
            {
                ItemId = 10093393,
                Quantity = 3,
                ExpirationBeijing = DateTime.MaxValue,
                Mode = DailyRefillMode.RefillToTarget,
            };

            var passed = appended.Count == 1
                && ticket != null
                && ticket.Quantity == 5
                && ticket.Mode == DailyRefillMode.RefillToTarget
                && DailyRefillItemPolicy.CalculateGrant(ticket, 0, 5) == 5
                && DailyRefillItemPolicy.CalculateGrant(ticket, 3, 5) == 2
                && DailyRefillItemPolicy.CalculateGrant(ticket, 5, 5) == 0
                && DailyRefillItemPolicy.CalculateGrant(ticket, 0, -1) == 5
                && DailyRefillItemPolicy.CalculateGrant(ticket, 3, -1) == 2
                && DailyRefillItemPolicy.CalculateGrant(unlimitedRule, 0, -1) == 3
                && DailyRefillItemPolicy.CalculateGrant(unlimitedRule, 3, -1) == 0
                && existing.Count(rule => rule.ItemId == ticketId) == 1
                && existing.Single(rule => rule.ItemId == ticketId).Quantity == 3;

            Console.WriteLine(passed
                ? "FIXED_DAILY_TICKET selftest passed"
                : "FIXED_DAILY_TICKET selftest failed");
            return passed ? 0 : 1;
        }
    }
}
