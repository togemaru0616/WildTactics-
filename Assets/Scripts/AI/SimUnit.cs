using UnityEngine;

public struct SimUnit
{
    public int        Id;
    public AnimalType AnimalType;
    public int        Owner;
    public Vector2Int Pos;
    public int        CurrentHP;
    public int        MaxHP;
    public int        AttackPower;
    public bool       IsDead;
    public int        PoisonedTurns;
    public float      MoveTimer;    // リアルタイム: 次の移動行動までの残り時間（秒）
    public float      AttackTimer;  // リアルタイム: 次の自動攻撃までの残り時間（実ゲームの AutoAttackLoop 相当）
}
