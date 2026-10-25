namespace SokuReplayLab;

public enum CardKind { Draw, Observed, Use, Cost, UnknownLoss, UnknownCost }

public sealed record CardEvent(int Ordinal, int Round, int Side, int CardId,
    CardKind Kind, int Character, double Slots, double? SlotsBefore = null);

/// <summary>Reconstructs observed draw and release order from game card deques.</summary>
public static class CardTimeline
{
    private sealed record Prior(int Round, int[] Hand, int[] Used);
    private sealed record Resource(int Round, int Count, double Slots);

    public static IReadOnlyList<CardEvent> Build(IReadOnlyList<Frame> frames)
    {
        var result = new List<CardEvent>();
        var prior = new Prior?[2];
        var resources = new Resource?[2];
        var lastRound = new int?[2];
        var lastHand = new int[2][] { [], [] };
        foreach (var frame in frames)
        {
            for (var side = 0; side < 2; side++)
            {
                var fighter = frame.Side(side);
                if (frame.Round < 0 || frame.MatchState != 2)
                {
                    prior[side] = null;
                    resources[side] = null;
                    lastRound[side] = null;
                    lastHand[side] = [];
                    continue;
                }
                var slots = Math.Min(5, fighter.CardCount + Math.Clamp(fighter.CardGauge, 0, 500) / 500.0);
                var oldResource = resources[side];
                resources[side] = new Resource(frame.Round, fighter.CardCount, slots);
                var hand = fighter.HandIds;
                var used = fighter.UsedIds;
                if (hand is null || used is null)
                {
                    if (oldResource?.Round == frame.Round)
                        AddUnknownLosses(Math.Max(0, oldResource.Count - fighter.CardCount), 0);
                    prior[side] = null;
                    lastRound[side] = frame.Round;
                    continue;
                }
                var old = prior[side];
                var fresh = old is null || old.Round != frame.Round;
                int[] newDraws;
                int[] newUses;
                int[] observed = [];
                if (fresh)
                {
                    newUses = [];
                    if (lastRound[side] != frame.Round) newDraws = hand;
                    else
                    {
                        newDraws = [];
                        var known = lastHand[side].GroupBy(x => x)
                            .ToDictionary(x => x.Key, x => x.Count());
                        observed = hand.Where(card => {
                            var remaining = known.GetValueOrDefault(card);
                            if (remaining == 0) return true;
                            known[card] = remaining - 1;
                            return false;
                        }).ToArray();
                    }
                }
                else
                {
                    var prefix = used.Length >= old!.Used.Length &&
                        used.AsSpan(0, old.Used.Length).SequenceEqual(old.Used);
                    newUses = prefix ? used[old.Used.Length..] : [];
                    var drawCount = Math.Max(0, hand.Length - old.Hand.Length + newUses.Length);
                    newDraws = drawCount == 0 ? [] : hand.TakeLast(drawCount).ToArray();
                }
                for (var i = 0; i < newUses.Length; i++)
                    result.Add(new CardEvent(frame.Ordinal, frame.Round, side, newUses[i],
                        i == 0 ? CardKind.Use : CardKind.Cost, fighter.Character,
                        slots, oldResource?.Round == frame.Round ? oldResource.Slots : slots));
                if (oldResource?.Round == frame.Round)
                    AddUnknownLosses(Math.Max(0, oldResource.Count - fighter.CardCount - newUses.Length), newUses.Length);
                foreach (var card in newDraws)
                    result.Add(new CardEvent(frame.Ordinal, frame.Round, side, card,
                        CardKind.Draw, fighter.Character, slots));
                foreach (var card in observed)
                    result.Add(new CardEvent(frame.Ordinal, frame.Round, side, card,
                        CardKind.Observed, fighter.Character, slots));
                prior[side] = new Prior(frame.Round, hand, used);
                lastRound[side] = frame.Round;
                lastHand[side] = hand;

                void AddUnknownLosses(int count, int knownUses)
                {
                    var spell = fighter.Action is >= 600 and <= 619;
                    for (var i = 0; i < count; i++)
                        result.Add(new CardEvent(frame.Ordinal, frame.Round, side, -1,
                            spell && (knownUses > 0 || i > 0) ? CardKind.UnknownCost : CardKind.UnknownLoss,
                            fighter.Character, slots, oldResource?.Slots));
                }
            }
        }
        return result;
    }

    public static string KindLabel(CardKind kind) => kind switch {
        CardKind.Draw => "抽到", CardKind.Observed => "首次观察",
        CardKind.Use => "使用", CardKind.Cost => "符卡代价",
        CardKind.UnknownCost => "代价未确认", _ => "流失未确认"
    };

    public static string Name(CardEvent card) => CardName(card.Character, card.CardId);

    public static string CardName(int character, int cardId) => cardId switch {
        < 0 => "牌身份未知",
        12 => "隔夜钱／铜钱",
        >= 100 and <= 219 => CardNameReference.Names.TryGetValue((character, cardId), out var name)
            ? name : cardId >= 200 ? $"符卡 {cardId}" : $"技能卡 {cardId}",
        _ => $"系统卡 {cardId}"
    };
}
