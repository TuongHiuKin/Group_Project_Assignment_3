using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIVES.DAL.Entities;
public class VivaRubricCriterion
{
    public Guid Id { get; set; }
    public int CriterionCode { get; set; }
    public string CriterionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public decimal MaxPoints { get; set; }

}

