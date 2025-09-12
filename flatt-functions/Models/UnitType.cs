#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;

namespace flatt_functions.Models
{
    [Table("UnitTypes")]
    public class UnitType
    {
        [Key]
        public int TypeID { get; set; }

        public string? TypeName { get; set; }
        public string? Description { get; set; }

        // Navigation property
        public virtual ICollection<Unit> Units { get; set; } = new List<Unit>();
    }
}