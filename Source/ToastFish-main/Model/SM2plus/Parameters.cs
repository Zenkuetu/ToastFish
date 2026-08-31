using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToastFish.Model.SM2plus
{
    public static class Parameters
    {
        public const double None = 0;  //performance rating
        public const double Again = 0.5;     // 2026-07-21 从 0.4 提升，减小失败惩罚
        public const double Hard = 0.7;     // 2026-07-21 从 0.6 提升，不再掉到 Again 惩罚区
        public const double Good = 0.8;
        public const double Easy = 1;
        public const double Correct = 0.8;  // 2026-07-21 从 0.7 提升，提高"正确"门槛以降低总正确率
        public const double delayAgain = 1; // delay minitues for again anser to new card
        public const double delayHard = 5.5; // delay minitues for Good answer to new card
        public const double delayGood = 10; // delay minitues for Hard answer to new card
        public const double delayReviewAgain = 10; // delay minitues for Again answer to the reviewed Card 
        public const double delayReviewHard = 15; // delay minitues for Hard answer to new card
        public const double delayReviewGood = 15; // delay minitues for Again answer to the reviewed Card
        public const double diffcultyDefaultValue = 0.3; // delay minitues for Good answer 
        public const double daysBetweenReviewsDefaultValue = 3; // delay minitues for Good answer 
    }
}
