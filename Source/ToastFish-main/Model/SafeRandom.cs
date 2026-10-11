using System;

namespace ToastFish.Model
{
    /// <summary>
    /// 线程安全的 Random 替代品（2026-10-08 新增）。
    ///
    /// 背景：全库 20 处 new Random()，分两类隐患 ——
    ///   ① **跨线程共享**：EssayGenerator._shuffleRng / SurpriseReviewManager._rng /
    ///      Card._rnd / Select._random 都是 static 共享实例，而 System.Random
    ///      **不是线程安全**的 —— 并发调用会损坏内部状态，.NET Framework 下
    ///      典型表现是 Next() 恒返回 0。CLAUDE.md #13 / #14 记录过这个隐患。
    ///   ② **同毫秒重复种子**：在方法内（甚至在循环内）new Random() 时，.NET Framework
    ///      用 Environment.TickCount 作种子，同一毫秒内创建的多个实例会得到
    ///      **完全相同的序列**（如 PushGoinWords 的 while 循环里每轮都 new Random()）。
    ///
    /// 做法：继承 Random，把**所有取值方法转发到一个 [ThreadStatic] 的内部实例**。
    ///   · 每个线程独享一个 Random  → 并发安全，无需加锁
    ///   · 每线程只创建一次          → 种子只取一次，杜绝同毫秒重复序列
    ///   · 因为继承 Random           → **全部调用点一行不改**，只把 new Random()
    ///                                换成 new SafeRandom() 即可
    ///
    /// ⚠️ 本文件内部必须保留 new Random(seed)（带参形式），不要替换成 SafeRandom，
    /// 否则会自我无限递归 → 栈溢出（0xc00000fd）。参见 CHANGELOG #76 的同类教训。
    /// </summary>
    internal sealed class SafeRandom : Random
    {
        [ThreadStatic] private static Random _inner;

        private static Random Inner
        {
            get
            {
                if (_inner == null)
                {
                    // 每个线程用不同种子，避免多个线程拿到相同序列
                    _inner = new Random(unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode()));
                }
                return _inner;
            }
        }

        public override int Next()
        {
            return Inner.Next();
        }

        public override int Next(int maxValue)
        {
            return Inner.Next(maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            return Inner.Next(minValue, maxValue);
        }

        public override double NextDouble()
        {
            return Inner.NextDouble();
        }

        public override void NextBytes(byte[] buffer)
        {
            Inner.NextBytes(buffer);
        }

        protected override double Sample()
        {
            return Inner.NextDouble();
        }
    }
}
