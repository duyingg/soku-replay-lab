namespace SokuReplayLab;

public static class ReplayState
{
    public static bool Matches(Frame baseline, Frame branch)
    {
        if (baseline.MatchState != branch.MatchState || baseline.Round != branch.Round ||
            baseline.Weather != branch.Weather ||
            baseline.EngineFrame != branch.EngineFrame)
            return false;
        for (var side = 0; side < 2; side++)
        {
            var expected = baseline.Side(side);
            var actual = branch.Side(side);
            if (expected.Character != actual.Character ||
                expected.Action != actual.Action ||
                expected.ActionFrame != actual.ActionFrame ||
                expected.Hp != actual.Hp || expected.Spirit != actual.Spirit ||
                expected.Input != actual.Input ||
                expected.ComboDamage != actual.ComboDamage ||
                expected.CardCount != actual.CardCount ||
                expected.CardGauge != actual.CardGauge ||
                expected.HitStop != actual.HitStop ||
                expected.Collision != actual.Collision ||
                Math.Abs(expected.X - actual.X) > 0.1 ||
                Math.Abs(expected.Y - actual.Y) > 0.1 ||
                expected.HandIds is not null && actual.HandIds is not null &&
                !expected.HandIds.SequenceEqual(actual.HandIds) ||
                expected.UsedIds is not null && actual.UsedIds is not null &&
                !expected.UsedIds.SequenceEqual(actual.UsedIds))
                return false;
        }
        return true;
    }

}
