using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIVES.DAL.Entities;
public class VivaRubricCriterion
{
    public string Id { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public int CriterionCode { get; set; }
    public string CriterionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public decimal MaxPoints { get; set; }

}

