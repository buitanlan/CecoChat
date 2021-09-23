﻿using Microsoft.OpenApi.Models;

namespace CecoChat.Swagger
{
    public sealed class SwaggerOptions
    {
        public bool UseSwagger { get; set; }

        public bool UseSwaggerUI { get; set; }

        public string Url { get; set; }

        public bool AddAuthorizationHeader { get; set; }

        public OpenApiInfo OpenApiInfo { get; set; }
    }
}
