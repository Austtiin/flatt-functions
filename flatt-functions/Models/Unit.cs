#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace flatt_functions.Models
{
    [Table("Units")]
    public class Unit
    {
        [Key]
        public int UnitID { get; set; }

        public string? Name { get; set; }
        public string? Model { get; set; }
        public int? Year { get; set; }
        public decimal? Price { get; set; }
        public string? Description { get; set; }

        [Column("UnitStatus")]
        public string? Status { get; set; }

        public int TypeID { get; set; }
        public virtual UnitType? UnitType { get; set; }

        public virtual ICollection<UnitFeature> UnitFeatures { get; set; } = new List<UnitFeature>();
        public virtual ICollection<UnitImage> UnitImages { get; set; } = new List<UnitImage>();
    }
}