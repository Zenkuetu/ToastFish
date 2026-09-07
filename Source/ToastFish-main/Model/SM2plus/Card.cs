using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ToastFish.Model.SqliteControl;

namespace ToastFish.Model.SM2plus
{
    public enum Cardstatus
    {
        New = 0,
        Step1 = 1,
        Step2 = 2,
        RelearnStep1 = 3,
        RelearnStep2 = 4,
        Reviewed = 5
    }
    public class Card
    {
        private static readonly Random _rnd = new Random();
        public double difficulty { get; set; }
        public double daysBetweenReviews { get; set; }
        public DateTime dateLastReviewed { get; set; }
        public DateTime dateLearingDue { get; set; }
        public double lastScore { get; set; }
        public Cardstatus status;
        public Word word;

        public double percentOverdue
        {
            get
            {
                double podue = 0;
                if (status == Cardstatus.Reviewed)
                {
                    bool correct = lastScore >= Parameters.Correct;
                    TimeSpan daysSpan = new TimeSpan(DateTime.Now.Ticks - dateLastReviewed.Ticks);

                    if (correct)
                        podue = Math.Min(2, daysSpan.TotalDays / Math.Max(0.01, daysBetweenReviews));
                    else
                        podue = 1;
                }
                else if (status == Cardstatus.New || status == Cardstatus.Step1)
                {
                    // 新卡无逾期计算，返回默认值 0
                }

                return podue;
            }
        }
        public Card()
        {
            difficulty = Parameters.diffcultyDefaultValue;
            daysBetweenReviews = Parameters.daysBetweenReviewsDefaultValue;
            lastScore = 0;
            status = Cardstatus.New;
        }
        public Card(Word wd)
        {
            word = wd;
            difficulty = wd.difficulty;
            daysBetweenReviews = wd.daysBetweenReviews;
            lastScore = wd.lastScore;
            status = (Cardstatus)wd.status;

            bool isSuccess1;
            if (status == Cardstatus.Reviewed)
            {
                if (!string.IsNullOrEmpty(wd.dateLastReviewed))
                {
                    isSuccess1 = DateTime.TryParseExact(wd.dateLastReviewed,
                        new[] { "yyyy/M/d H:m:s", "yyyy/M/d H:mm:ss", "yyyy/MM/dd H:m:s", "yyyy/MM/dd H:mm:ss" },
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime tempDT);
                    if (isSuccess1)
                        dateLastReviewed = tempDT;
                    else
                        dateLastReviewed = DateTime.Now;
                }
            }
            else if (status != Cardstatus.New)
            {
                // Step1/Step2/RelearnStep1/RelearnStep2：解析学习中的下次到期时间
                if (!string.IsNullOrEmpty(wd.dateLearingDue))
                {
                    bool isSuccess2 = DateTime.TryParseExact(wd.dateLearingDue,
                        new[] { "yyyy/M/d H:m:s", "yyyy/M/d H:mm:ss", "yyyy/MM/dd H:m:s", "yyyy/MM/dd H:mm:ss" },
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime tempDue);
                    if (isSuccess2)
                        dateLearingDue = tempDue;
                }
            }

        }

        public void reset()
        {
            difficulty = Parameters.diffcultyDefaultValue;
            daysBetweenReviews = Parameters.daysBetweenReviewsDefaultValue;
            lastScore = Parameters.None;
        }

        public bool isDue()
        {
            bool rst = false;
            DateTime curTime = DateTime.Now;
            if (curTime >= dateLearingDue)
                rst = true;
            return rst;
        }


        public void updateCard(double curScore)
        {
            bool isLapsed = false;
            switch (status)
            {
                case Cardstatus.New:
                case Cardstatus.Step1:
                    if (curScore == Parameters.Easy)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Good)
                    {
                        status = Cardstatus.Step2;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayGood);
                    }
                    else if (curScore == Parameters.Hard)
                    {
                        status = Cardstatus.Step1;  // 修复：新词答 Hard 应进入 Step1，而非停留在 New(0) 导致不计入已学
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayHard);
                    }
                    else if (curScore == Parameters.Again)
                    {
                        status = Cardstatus.Step1;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayAgain);
                    }
                    break;
                case Cardstatus.Step2:
                    if (curScore == Parameters.Easy)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Good)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Hard)
                    {
                        //status = Cardstatus.Step2;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayHard);
                    }
                    else if (curScore == Parameters.Again)
                    {
                        status = Cardstatus.Step1;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayAgain);
                    }
                    break;
                case Cardstatus.Reviewed:
                    if (curScore == Parameters.Again)
                    {
                        status = Cardstatus.RelearnStep1;
                        isLapsed = true;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewAgain);
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Hard)
                    {
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Good)
                    {
                        lastScore = curScore;
                    }
                    else if (curScore == Parameters.Easy)
                    {
                        lastScore = curScore;
                    }
                    break;
                case Cardstatus.RelearnStep1:
                    //isLapsed = true; do not update last score for relearn steps
                    if (curScore == Parameters.Easy)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                    }
                    else if (curScore == Parameters.Good)
                    {
                        status = Cardstatus.RelearnStep2;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewGood);
                    }
                    else if (curScore == Parameters.Hard)
                    {
                        //status = Cardstatus.RelearnStep1;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewHard);
                    }
                    else if (curScore == Parameters.Again)
                    {
                        status = Cardstatus.RelearnStep1;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewAgain);
                    }
                    break;
                case Cardstatus.RelearnStep2:
                    //isLapsed = true;
                    if (curScore == Parameters.Easy)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                    }
                    else if (curScore == Parameters.Good)
                    {
                        status = Cardstatus.Reviewed;
                        dateLastReviewed = DateTime.Now;
                    }
                    else if (curScore == Parameters.Hard)
                    {
                        //status = Cardstatus.RelearnStep2;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewHard);
                    }
                    else if (curScore == Parameters.Again)
                    {
                        status = Cardstatus.RelearnStep1;
                        dateLearingDue = DateTime.Now.AddMinutes(Parameters.delayReviewAgain);
                    }
                    break;
                default:
                    break;
            }
            if ((status != Cardstatus.Reviewed) && (isLapsed != true)) return;

            // BUG FIX: 保存旧的 dateLastReviewed 用于计算已过天数
            // 之前这里先设 dateLastReviewed = DateTime.Now，再拿它算 daysSpan，结果永远是 0
            // 导致 difficulty 和 daysBetweenReviews 永远不变
            DateTime oldLastReviewed = dateLastReviewed;
            dateLastReviewed = DateTime.Now;

            bool correct = curScore >= Parameters.Correct;
            TimeSpan daysSpan;
            if (oldLastReviewed != default(DateTime) && oldLastReviewed < dateLastReviewed)
                daysSpan = new TimeSpan(dateLastReviewed.Ticks - oldLastReviewed.Ticks);
            else
                daysSpan = TimeSpan.Zero; // 首次复习，距上次间隔视为 0

            double podue;
            if (correct)
                podue = Math.Min(2, daysSpan.TotalDays / Math.Max(0.01, daysBetweenReviews));
            else
                podue = 1;
            difficulty += podue * (8 - 10 * curScore) / 17;
            if (difficulty < 0)
                difficulty = 0;
            if (difficulty > 1)
                difficulty = 1;
            double dfweight = 3.5 - 1.7 * difficulty; // 2026-07-21 从 3 提升，加速低难度词间隔扩张
            Random rnd = _rnd;
            if (correct)
                daysBetweenReviews *= (1 + (dfweight - 1) * podue * (0.95 + 0.1 * rnd.NextDouble()));
            else
                daysBetweenReviews *= 1 / (1 + 3 * difficulty);
        }
    }
}
