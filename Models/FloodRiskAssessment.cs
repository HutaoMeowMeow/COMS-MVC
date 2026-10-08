using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class FloodRiskAssessment
    {
        public int FloodRiskAssessmentId { get; set; }

        [Display(Name = "Canal")]
        public int CanalId { get; set; }

        [Display(Name = "Risk Score")]
        public int RiskScore { get; set; }

        [Display(Name = "Risk Level")]
        public string RiskLevel { get; set; }

        [Display(Name = "Assessment Details")]
        public string AssessmentDetails { get; set; }

        [Display(Name = "Model Version")]
        public string ModelVersion { get; set; } = "Rule-Based v1.0";

        [Display(Name = "Assessment Date")]
        public DateTime AssessmentDate { get; set; }

        [Display(Name = "Valid Until")]
        public DateTime ValidUntil { get; set; }

        public Canal? Canal { get; set; }
    }
}
