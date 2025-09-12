#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace flatt_functions.Models
{
    [Table("UnitImages")]
    public class UnitImage
    {
        [Key]
        public int ImageID { get; set; }

        public int UnitID { get; set; }
        public string? ImageURL { get; set; }
        public int DisplayOrder { get; set; }
        public string? AltText { get; set; }

        // Navigation property
        public virtual Unit? Unit { get; set; }
    }
}