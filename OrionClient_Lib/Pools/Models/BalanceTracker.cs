using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.Models
{
    internal class BalanceTracker<T> where T : INumber<T>
    {
        public T InitialBalance { get; private set; }
        public DateTime InitialTime { get; private set; }

        public T OldBalance { get; private set; }
        public DateTime OldTime { get; private set; }

        public T CurrentBalance { get; private set; }
        public DateTime CurrentTime { get; private set; }
        private bool _initial = false;


        public T TotalChange => CurrentBalance - InitialBalance;
        public TimeSpan TotalTime => CurrentTime - InitialTime;

        public T BalanceChangeSinceUpdate => CurrentBalance - OldBalance;
        public TimeSpan TimeChangeSinceUpdate => CurrentTime - OldTime;

        public void Update(T value)
        {
            OldBalance = CurrentBalance;
            OldTime = CurrentTime;

            CurrentBalance = value;
            CurrentTime = DateTime.UtcNow;

            if (!_initial)
            {
                _initial = true;

                InitialBalance = value;
                InitialTime = DateTime.UtcNow;

                OldBalance = CurrentBalance;
                OldTime = CurrentTime;
            }
        }

        public override string ToString()
        {
            return $"{CurrentBalance:0.00000000000}";
        }
    }
}
