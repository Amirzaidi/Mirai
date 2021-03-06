﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mirai.Database.Tables
{
    class Song
    {
        [Key, Column(Order = 1)]
        public byte Feed { get; set; }

        [Key, Column(Order = 2)]
        public int Place { get; set; }
        public string Query { get; set; }
    }
}
