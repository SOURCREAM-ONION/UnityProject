/// <summary>
/// 진영 목록. 플레이어는 모든 몬스터 진영과 적대하고,
/// 몬스터 진영끼리는 FactionManager의 rivalries에 넣은 쌍만 서로 싸운다.
/// </summary>
public enum Faction
{
    Player,
    Goblin,
    Orc,
    Undead,
    Beast
}
