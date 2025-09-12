#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace flatt_functions.Models
{
    [Table("UnitFeatures")]
    public class UnitFeature
    {
        [Key]
        public int FeatureID { get; set; }

        public int UnitID { get; set; }
        public string? FeatureName { get; set; }
        public string? Description { get; set; }

        // Navigation property
        public virtual Unit? Unit { get; set; }
    }
}